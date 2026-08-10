using ChronicleDB.Transactions;
using ChronicleDB.Transactions.State;

namespace ChronicleDB;

/// <summary>
/// Public transaction handle. Transaction operations are synchronized internally, but
/// applications should treat one handle as single-owner; database-level concurrency is
/// achieved with independent transaction handles.
/// </summary>
public sealed class ChronicleTransaction : IDisposable
{
    private readonly ChronicleDatabase _database;
    private readonly Transaction _transaction;
    private int _completedHandle;

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
            if (IsTerminal(_transaction.State))
            {
                CompleteHandle();
            }
        }
    }

    public void Abort()
    {
        ThrowIfDisposed();
        _database.Abort(_transaction, throwIfNotAbortable: true);
        CompleteHandle();
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _completedHandle) != 0)
        {
            return;
        }

        _database.Abort(_transaction, throwIfNotAbortable: false);
        CompleteHandle();
    }

    private void CompleteHandle()
    {
        if (Interlocked.Exchange(ref _completedHandle, 1) == 0)
        {
            _database.TransactionHandleCompleted();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _completedHandle) != 0, this);

    private static bool IsTerminal(TransactionState state)
        => state is TransactionState.Committed
            or TransactionState.Aborted
            or TransactionState.DurableCommitted
            or TransactionState.Indeterminate;
}
