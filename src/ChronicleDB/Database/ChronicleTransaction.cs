using ChronicleDB.Transactions;
using ChronicleDB.Transactions.State;

namespace ChronicleDB;

/// <summary>
/// Public transaction handle. Independent handles may execute concurrently. Operations
/// on one handle are serialized so commit cannot race an abort/dispose transition on
/// the same transaction descriptor.
/// </summary>
public sealed class ChronicleTransaction : IDisposable
{
    private readonly ITransactionHost _host;
    private readonly Transaction _transaction;
    private readonly object _handleGate = new();
    private int _completedHandle;

    internal ChronicleTransaction(ITransactionHost host, Transaction transaction)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _transaction = transaction;
    }

    public Guid TransactionId => _transaction.TransactionId.Value;

    public Guid HistoryId => _transaction.HistoryId.Value;

    public ulong StartSequence => _transaction.StartSequence.Value;

    public ulong? CommitSequence => _transaction.CommitSequence?.Value;

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        lock (_handleGate)
        {
            ThrowIfDisposed();
            _transaction.Put(key, value);
        }
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        lock (_handleGate)
        {
            ThrowIfDisposed();
            _transaction.Delete(key);
        }
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_handleGate)
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

            return _host.ReadAt(key, _transaction.StartSequence, out value);
        }
    }

    public void Commit()
    {
        lock (_handleGate)
        {
            ThrowIfDisposed();
            try
            {
                _host.Commit(_transaction);
            }
            finally
            {
                if (IsTerminal(_transaction.State))
                {
                    CompleteHandle();
                }
            }
        }
    }

    public void Abort()
    {
        lock (_handleGate)
        {
            ThrowIfDisposed();
            _host.Abort(_transaction, throwIfNotAbortable: true);
            CompleteHandle();
        }
    }

    public void Dispose()
    {
        lock (_handleGate)
        {
            if (Volatile.Read(ref _completedHandle) != 0)
            {
                return;
            }

            _host.Abort(_transaction, throwIfNotAbortable: false);
            CompleteHandle();
        }
    }

    private void CompleteHandle()
    {
        if (Interlocked.Exchange(ref _completedHandle, 1) == 0)
        {
            _host.TransactionHandleCompleted();
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
