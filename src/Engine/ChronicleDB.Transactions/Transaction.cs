using System.Diagnostics.CodeAnalysis;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Transactions.State;
using ChronicleDB.Transactions.Writes;

namespace ChronicleDB.Transactions;

/// <summary>
/// Snapshot Isolation transaction descriptor. It owns a private write set until the owning history commits or aborts it.
/// </summary>
public sealed class Transaction
{
    private readonly object _gate = new();
    private readonly Dictionary<BinaryKey, TransactionWrite> _writes = [];
    private TransactionState _state = TransactionState.Created;
    private CommitSequence? _commitSequence;

    public Transaction(
        TransactionId? transactionId = null,
        CommitSequence startSequence = default,
        HistoryId historyId = default)
    {
        TransactionId = transactionId ?? TransactionId.New();
        if (!TransactionId.IsValid)
        {
            throw new ArgumentException("A transaction requires a non-empty transaction ID.", nameof(transactionId));
        }

        StartSequence = startSequence;
        HistoryId = historyId;
    }

    public TransactionId TransactionId { get; }

    /// <summary>
    /// Logical history domain that owns this transaction. Empty is permitted only
    /// for low-level tests and legacy direct use of the transaction descriptor.
    /// </summary>
    public HistoryId HistoryId { get; }

    public CommitSequence StartSequence { get; }

    public CommitSequence? CommitSequence
    {
        get
        {
            lock (_gate)
            {
                return _commitSequence;
            }
        }
    }

    public TransactionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int WriteCount
    {
        get
        {
            lock (_gate)
            {
                return _writes.Count;
            }
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            Transition(TransactionState.Created, TransactionState.Active);
        }
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        lock (_gate)
        {
            RequireActive();
            var ownedKey = new BinaryKey(key);
            _writes[ownedKey] = new TransactionWrite(ownedKey, isDelete: false, value);
        }
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        lock (_gate)
        {
            RequireActive();
            var ownedKey = new BinaryKey(key);
            _writes[ownedKey] = new TransactionWrite(ownedKey, isDelete: true, ReadOnlySpan<byte>.Empty);
        }
    }

    public bool TryGetLocal(ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            RequireReadable();
            if (!_writes.TryGetValue(new BinaryKey(key), out var write) || write.IsDelete)
            {
                value = [];
                return false;
            }

            value = write.Value.ToArray();
            return true;
        }
    }

    public bool TryGetLocalWrite(
        ReadOnlySpan<byte> key,
        [NotNullWhen(true)] out TransactionWrite? write)
    {
        lock (_gate)
        {
            RequireReadable();
            if (!_writes.TryGetValue(new BinaryKey(key), out var existing))
            {
                write = null;
                return false;
            }

            write = existing.Clone();
            return true;
        }
    }

    public IReadOnlyList<TransactionWrite> GetWriteSet()
    {
        lock (_gate)
        {
            RequireReadable();
            return _writes.Values.Select(write => write.Clone()).ToArray();
        }
    }

    public IReadOnlyList<TransactionWrite> PrepareAndGetWriteSet()
    {
        lock (_gate)
        {
            Transition(TransactionState.Active, TransactionState.Preparing);
            return _writes.Values.Select(write => write.Clone()).ToArray();
        }
    }

    public void Prepare()
    {
        lock (_gate)
        {
            Transition(TransactionState.Active, TransactionState.Preparing);
        }
    }

    public void MarkCommitting()
    {
        lock (_gate)
        {
            Transition(TransactionState.Preparing, TransactionState.Committing);
        }
    }

    public void MarkCommitted()
    {
        lock (_gate)
        {
            Transition(TransactionState.DurableCommitted, TransactionState.Committed);
            _writes.Clear();
        }
    }

    public void MarkDurableCommitted(CommitSequence commitSequence)
    {
        lock (_gate)
        {
            if (commitSequence.IsInitial)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commitSequence),
                    "A durable commit requires a non-zero commit sequence.");
            }

            Transition(TransactionState.Committing, TransactionState.DurableCommitted);
            _commitSequence = commitSequence;
        }
    }


    public void MarkIndeterminate()
    {
        lock (_gate)
        {
            if (_state is not (TransactionState.Preparing or TransactionState.Committing))
            {
                throw InvalidTransition(TransactionState.Indeterminate);
            }

            _state = TransactionState.Indeterminate;
            _writes.Clear();
        }
    }

    public void BeginAbort()
    {
        lock (_gate)
        {
            if (_state is not (TransactionState.Created
                or TransactionState.Active
                or TransactionState.Preparing))
            {
                throw InvalidTransition(TransactionState.Aborting);
            }

            _state = TransactionState.Aborting;
            _writes.Clear();
        }
    }

    public void MarkAborted()
    {
        lock (_gate)
        {
            Transition(TransactionState.Aborting, TransactionState.Aborted);
        }
    }

    public void Abort()
    {
        BeginAbort();
        MarkAborted();
    }

    private void RequireActive()
    {
        if (_state != TransactionState.Active)
        {
            throw InvalidTransition(TransactionState.Active);
        }
    }

    private void RequireReadable()
    {
        if (_state is not (TransactionState.Active or TransactionState.Preparing or TransactionState.Committing))
        {
            throw InvalidTransition(TransactionState.Active);
        }
    }

    private void Transition(TransactionState expected, TransactionState next)
    {
        if (_state != expected)
        {
            throw InvalidTransition(next);
        }

        _state = next;
    }

    private InvalidOperationException InvalidTransition(TransactionState target)
        => new($"Transaction {TransactionId.Value} cannot transition from {_state} to {target}.");
}
