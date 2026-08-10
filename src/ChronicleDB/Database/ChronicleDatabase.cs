using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Indexing.Baseline;
using ChronicleDB.Recovery;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Transactions;
using ChronicleDB.Transactions.Faults;
using ChronicleDB.Transactions.Mvcc;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB;

/// <summary>
/// Embedded key-value surface with v0.3 durable Snapshot Isolation transactions.
/// </summary>
public sealed class ChronicleDatabase : IDisposable
{
    private readonly PersistentKeyValueStore _store;
    private readonly WalLog _wal;
    private readonly CommittedVersionStore _versions;
    private readonly object _gate = new();
    private readonly ITransactionFaultInjector? _faultInjector;
    private CommitSequence _currentCommitSequence;
    private DatabaseState _state = DatabaseState.Open;

    private ChronicleDatabase(
        PersistentKeyValueStore store,
        WalLog wal,
        CommittedVersionStore versions,
        CommitSequence currentCommitSequence,
        ITransactionFaultInjector? faultInjector)
    {
        _store = store;
        _wal = wal;
        _versions = versions;
        _currentCommitSequence = currentCommitSequence;
        _faultInjector = faultInjector;
    }

    public Guid DatabaseId => _store.DatabaseId;

    public CommitSequence CurrentCommitSequence
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _currentCommitSequence;
            }
        }
    }

    public DatabaseState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _store.Count;
            }
        }
    }

    public static ChronicleDatabase Open(
        string directory,
        StorageOptions? options = null,
        ITransactionFaultInjector? faultInjector = null)
    {
        if (options is { MaxValueSize: > WalMutationCodec.MaxValueSize })
        {
            throw new StorageLimitException(
                $"ChronicleDB transactions support values up to {WalMutationCodec.MaxValueSize} bytes.");
        }

        var store = PersistentKeyValueStore.Open(directory, options, allowIncompleteFinalPage: true);
        WalLog? wal = null;
        try
        {
            wal = WalLog.Open(directory, store.DatabaseId, new WalOptions { FlushOnAppend = false });
            var recovery = WalRecovery.Reconcile(store, wal);
            var versions = new CommittedVersionStore(new SynchronizedVersionIndex());
            foreach (var transaction in recovery.CommittedTransactions.OrderBy(entry => entry.CommitLsn))
            {
                versions.ReplayCommitted(
                    transaction.TransactionId,
                    transaction.CommitSequence,
                    transaction.Mutations);
            }

            var currentCommitSequence = recovery.CurrentCommitSequence;
            var legacyCurrentState = store
                .SnapshotCurrentState()
                .Where(mutation => !versions.TryGetLatestCommitSequence(mutation.Key, out _))
                .ToArray();
            if (legacyCurrentState.Length != 0)
            {
                // v0.1 databases could contain physical current-state keys before WAL
                // existed. They have no historical sequence, so bootstrap them at the
                // current open boundary. v0.3 does not expose pre-open time travel.
                if (currentCommitSequence.IsInitial)
                {
                    currentCommitSequence = currentCommitSequence.Next();
                }

                versions.ReplayCommitted(
                    TransactionId.New(),
                    currentCommitSequence,
                    legacyCurrentState);
            }

            return new ChronicleDatabase(
                store,
                wal,
                versions,
                currentCommitSequence,
                faultInjector);
        }
        catch
        {
            try
            {
                wal?.Dispose();
            }
            finally
            {
                store.Dispose();
            }

            throw;
        }
    }

    public ChronicleTransaction BeginTransaction()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            var transaction = new Transaction(startSequence: _currentCommitSequence);
            transaction.Begin();
            return new ChronicleTransaction(this, transaction);
        }
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            using var transaction = BeginTransaction();
            transaction.Put(key, value);
            transaction.Commit();
        }
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _versions.TryRead(new BinaryKey(key), _currentCommitSequence, out value);
        }
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            var existed = _versions.TryRead(new BinaryKey(key), _currentCommitSequence, out _);
            using var transaction = BeginTransaction();
            transaction.Delete(key);
            transaction.Commit();
            return existed;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            _store.Flush();
            _wal.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == DatabaseState.Closed)
            {
                return;
            }

            try
            {
                _wal.Dispose();
            }
            finally
            {
                try
                {
                    _store.Dispose();
                }
                finally
                {
                    _state = DatabaseState.Closed;
                }
            }
        }
    }

    internal bool ReadAt(
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            if (visibilityBoundary > _currentCommitSequence)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(visibilityBoundary),
                    "A transaction cannot read beyond current committed history.");
            }

            return _versions.TryRead(new BinaryKey(key), visibilityBoundary, out value);
        }
    }

    internal void Commit(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            ThrowIfUsable();
            // Freeze the transaction and copy its final write set atomically with respect
            // to concurrent callers using the same transaction handle.
            var writes = transaction.PrepareAndGetWriteSet();
            List<StorageMutation> mutations;
            List<(WalRecordType Type, byte[] Payload)> walPayloads;
            CommitSequence commitSequence;
            byte[] commitPayload;

            try
            {
                ValidateWriteConflicts(transaction, writes);
                commitSequence = NextCommitSequence();

                mutations = new List<StorageMutation>(writes.Count);
                walPayloads = new List<(WalRecordType Type, byte[] Payload)>(writes.Count);
                foreach (var write in writes)
                {
                    if (write.IsDelete)
                    {
                        var payload = WalMutationCodec.EncodeDelete(write.Key);
                        ValidateWalPayload(payload);
                        walPayloads.Add((WalRecordType.Delete, payload));
                        mutations.Add(new StorageMutation(write.Key, isDelete: true, ReadOnlySpan<byte>.Empty));
                    }
                    else
                    {
                        var payload = WalMutationCodec.EncodePut(write.Key, write.Value.Span);
                        ValidateWalPayload(payload);
                        walPayloads.Add((WalRecordType.Put, payload));
                        mutations.Add(new StorageMutation(write.Key, isDelete: false, write.Value.Span));
                    }
                }

                // Validate every deterministic representation before the first WAL byte is
                // appended. Once the Commit record is durable, failure requires recovery.
                _store.ValidateBatch(mutations);
                ValidateWalCapacity(walPayloads.Count + 2);
                var baseDataLength = _store.DataLength;
                commitPayload = WalCommitCodec.Encode(commitSequence, baseDataLength);
            }
            catch
            {
                if (transaction.State == Transactions.State.TransactionState.Preparing)
                {
                    transaction.Abort();
                }

                throw;
            }

            var walTouched = false;
            try
            {
                _faultInjector?.Hit(TransactionFaultPoint.BeforeWalAppend);
                // An append can fail after writing only a prefix. Treat the WAL as touched
                // before issuing the I/O so the instance is not reused ambiguously.
                walTouched = true;
                _wal.Append(WalRecordType.Begin, transaction.TransactionId, []);
                foreach (var (type, payload) in walPayloads)
                {
                    _wal.Append(type, transaction.TransactionId, payload);
                }

                transaction.MarkCommitting();
                _wal.Append(WalRecordType.Commit, transaction.TransactionId, commitPayload);
                _faultInjector?.Hit(TransactionFaultPoint.AfterWalAppend);
                _faultInjector?.Hit(TransactionFaultPoint.BeforeWalFlush);
                _wal.Flush();
                transaction.MarkDurableCommitted(commitSequence);
                _faultInjector?.Hit(TransactionFaultPoint.AfterWalFlush);
                _faultInjector?.Hit(TransactionFaultPoint.BeforePhysicalPublication);
                _store.ApplyBatch(mutations);
                _faultInjector?.Hit(TransactionFaultPoint.AfterPhysicalPublication);

                // v0.3 serializes commit publication under the database gate. The index
                // heads may be installed one by one physically, but no reader can observe
                // the intermediate state. v0.4 can replace this with a descriptor/CAS path.
                _versions.PublishCommitted(transaction.TransactionId, commitSequence, writes);
                transaction.MarkCommitted();
                _currentCommitSequence = commitSequence;
                _faultInjector?.Hit(TransactionFaultPoint.BeforeAcknowledgement);
            }
            catch
            {
                if (walTouched)
                {
                    _state = DatabaseState.Faulted;
                }
                else if (transaction.State == Transactions.State.TransactionState.Preparing)
                {
                    // A failure before the first WAL append has no durable ambiguity.
                    // Complete the local abort so callers do not inherit a transaction
                    // permanently stuck in Preparing.
                    transaction.Abort();
                }

                throw;
            }
        }
    }

    internal void Abort(Transaction transaction, bool throwIfNotAbortable)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            // Abort is serialized with Commit so a caller cannot move a transaction out
            // of Committing while another thread is crossing the WAL durability barrier.
            var state = transaction.State;
            if (state is Transactions.State.TransactionState.Created
                or Transactions.State.TransactionState.Active
                or Transactions.State.TransactionState.Preparing
                or Transactions.State.TransactionState.Committing)
            {
                transaction.Abort();
                return;
            }

            if (throwIfNotAbortable)
            {
                throw new InvalidOperationException(
                    $"Transaction {transaction.TransactionId.Value} cannot be aborted from {state}.");
            }
        }
    }

    private void ValidateWriteConflicts(
        Transaction transaction,
        IReadOnlyList<Transactions.Writes.TransactionWrite> writes)
    {
        foreach (var write in writes)
        {
            if (_versions.TryGetLatestCommitSequence(write.Key, out var latest)
                && latest > transaction.StartSequence)
            {
                throw new TransactionConflictException(
                    transaction.TransactionId.Value,
                    transaction.StartSequence.Value,
                    latest.Value);
            }
        }
    }

    private CommitSequence NextCommitSequence()
    {
        try
        {
            return _currentCommitSequence.Next();
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The commit-sequence space is exhausted.", exception);
        }
    }

    private void ThrowIfUsable()
    {
        if (_state == DatabaseState.Closed)
        {
            ObjectDisposedException.ThrowIf(true, this);
        }

        if (_state == DatabaseState.Faulted)
        {
            throw new ChronicleDatabaseFaultedException();
        }
    }

    private static void ValidateWalPayload(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > WalMutationCodec.MaxRecordPayloadSize)
        {
            throw new ChronicleDB.Wal.Errors.WalLimitException(
                "The mutation does not fit in the maximum WAL record payload.");
        }
    }

    private void ValidateWalCapacity(int recordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordCount);
        var required = checked((ulong)recordCount);
        var nextLsn = _wal.NextLsn;
        if (nextLsn > ulong.MaxValue - required)
        {
            throw new ChronicleDB.Wal.Errors.WalLimitException(
                "The WAL LSN space cannot fit the complete transaction.");
        }
    }
}
