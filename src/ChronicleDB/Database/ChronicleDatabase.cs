using ChronicleDB.Core.Keys;
using ChronicleDB.Recovery;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Transactions;
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
    private bool _disposed;

    private ChronicleDatabase(PersistentKeyValueStore store, WalLog wal)
    {
        _store = store;
        _wal = wal;
    }

    public Guid DatabaseId => _store.DatabaseId;

    public int Count => _store.Count;

    public static ChronicleDatabase Open(
        string directory,
        StorageOptions? options = null)
    {
        var store = PersistentKeyValueStore.Open(directory, options);
        WalLog? wal = null;
        try
        {
            wal = WalLog.Open(directory);
            WalRecovery.Reconcile(store, wal);
            return new ChronicleDatabase(store, wal);
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
            ThrowIfDisposed();
            var transaction = new Transaction();
            transaction.Begin();
            return new ChronicleTransaction(this, transaction);
        }
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = BeginTransaction();
            transaction.Put(key, value);
            transaction.Commit();
        }
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _store.TryGet(new BinaryKey(key), out value);
        }
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
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
            ThrowIfDisposed();
            _store.Flush();
            _wal.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _wal.Dispose();
            _store.Dispose();
            _disposed = true;
        }
    }

    internal bool ReadCommitted(ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _store.TryGet(new BinaryKey(key), out value);
        }
    }

    internal void Commit(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            ThrowIfDisposed();
            transaction.Prepare();
            var writes = transaction.GetWriteSet();
            _wal.Append(WalRecordType.Begin, transaction.TransactionId, []);
            var mutations = new List<StorageMutation>(writes.Count);
            foreach (var write in writes)
            {
                if (write.IsDelete)
                {
                    _wal.Append(WalRecordType.Delete, transaction.TransactionId, WalMutationCodec.EncodeDelete(write.Key));
                    mutations.Add(new StorageMutation(write.Key, isDelete: true, ReadOnlySpan<byte>.Empty));
                }
                else
                {
                    _wal.Append(WalRecordType.Put, transaction.TransactionId, WalMutationCodec.EncodePut(write.Key, write.Value.Span));
                    mutations.Add(new StorageMutation(write.Key, isDelete: false, write.Value.Span));
                }
            }

            transaction.MarkCommitting();
            _wal.Append(WalRecordType.Commit, transaction.TransactionId, []);
            _wal.Flush();
            _store.ApplyBatch(mutations);
            transaction.MarkCommitted();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
