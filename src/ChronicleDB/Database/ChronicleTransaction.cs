using ChronicleDB.Core.Keys;
using ChronicleDB.Transactions;

namespace ChronicleDB;

public sealed class ChronicleTransaction : IDisposable
{
    private readonly ChronicleDatabase _database;
    private readonly Transaction _transaction;
    private bool _disposed;

    internal ChronicleTransaction(ChronicleDatabase database, Transaction transaction)
    {
        _database = database;
        _transaction = transaction;
    }

    public Guid TransactionId => _transaction.TransactionId.Value;

    public ulong StartSequence => _transaction.StartSequence.Value;

    public ulong? CommitSequence => _transaction.CommitSequence?.Value;

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        _transaction.Put(key, value);
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        _transaction.Delete(key);
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        if (_transaction.TryGetLocalWrite(key, out var write))
        {
            if (write.IsDelete)
            {
                value = [];
                return false;
            }

            value = write.Value.ToArray();
            return true;
        }

        return _database.ReadAt(key, _transaction.StartSequence, out value);
    }

    public void Commit()
    {
        ThrowIfDisposed();
        try
        {
            _database.Commit(_transaction);
        }
        finally
        {
            if (_transaction.State is Transactions.State.TransactionState.Committed
                or Transactions.State.TransactionState.Aborted
                or Transactions.State.TransactionState.DurableCommitted)
            {
                _disposed = true;
            }
        }
    }

    public void Abort()
    {
        ThrowIfDisposed();
        _database.Abort(_transaction, throwIfNotAbortable: true);
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _database.Abort(_transaction, throwIfNotAbortable: false);

        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
