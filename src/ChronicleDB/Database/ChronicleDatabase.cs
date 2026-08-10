using ChronicleDB.Core.Keys;
using ChronicleDB.Recovery;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Transactions;
using ChronicleDB.Transactions.Faults;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB;

/// <summary>
/// Embedded key-value surface with v0.2 durable multi-key transactions.
/// </summary>
public sealed class ChronicleDatabase : IDisposable
{
    private readonly PersistentKeyValueStore _store;
    private readonly WalLog _wal;
    private readonly object _gate = new();
    private readonly ITransactionFaultInjector? _faultInjector;
    private DatabaseState _state = DatabaseState.Open;

    private ChronicleDatabase(
        PersistentKeyValueStore store,
        WalLog wal,
        ITransactionFaultInjector? faultInjector)
    {
        _store = store;
        _wal = wal;
        _faultInjector = faultInjector;
    }

    public Guid DatabaseId => _store.DatabaseId;

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
            WalRecovery.Reconcile(store, wal);
            return new ChronicleDatabase(store, wal, faultInjector);
        }
        catch
        {
            wal?.Dispose();
            store.Dispose();
            throw;
        }
    }

    public ChronicleTransaction BeginTransaction()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            var transaction = new Transaction();
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
            return _store.TryGet(new BinaryKey(key), out value);
        }
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            var existed = _store.TryGet(new BinaryKey(key), out _);
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
                _store.Dispose();
            }
            finally
            {
                _state = DatabaseState.Closed;
            }
        }
    }

    internal bool ReadCommitted(ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _store.TryGet(new BinaryKey(key), out value);
        }
    }

    internal void Commit(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            ThrowIfUsable();
            var writes = transaction.GetWriteSet();
            var mutations = new List<StorageMutation>(writes.Count);
            var walPayloads = new List<(WalRecordType Type, byte[] Payload)>(writes.Count);
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

            // Validate every representation before the first WAL byte is appended. This keeps
            // a rejected mutation from becoming a durable commit that recovery cannot replay.
            _store.ValidateBatch(mutations);
            transaction.Prepare();

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
                _wal.Append(WalRecordType.Commit, transaction.TransactionId, []);
                _faultInjector?.Hit(TransactionFaultPoint.AfterWalAppend);
                _faultInjector?.Hit(TransactionFaultPoint.BeforeWalFlush);
                _wal.Flush();
                transaction.MarkDurableCommitted();
                _faultInjector?.Hit(TransactionFaultPoint.AfterWalFlush);
                _faultInjector?.Hit(TransactionFaultPoint.BeforePhysicalPublication);
                _store.ApplyBatch(mutations);
                _faultInjector?.Hit(TransactionFaultPoint.AfterPhysicalPublication);
                transaction.MarkCommitted();
                _faultInjector?.Hit(TransactionFaultPoint.BeforeAcknowledgement);
            }
            catch
            {
                if (walTouched)
                {
                    _state = DatabaseState.Faulted;
                }

                throw;
            }
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
}
