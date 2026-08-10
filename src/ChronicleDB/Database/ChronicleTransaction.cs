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

        return _database.ReadCommitted(key, out value);
    }

    public void Commit()
    {
        ThrowIfDisposed();
        _database.Commit(_transaction);
        _disposed = true;
    }

    public void Abort()
    {
        ThrowIfDisposed();
        _transaction.Abort();
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_transaction.State is Transactions.State.TransactionState.Created
            or Transactions.State.TransactionState.Active
            or Transactions.State.TransactionState.Preparing
            or Transactions.State.TransactionState.Committing)
        {
            _transaction.Abort();
        }

        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
