using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Transactions;

namespace ChronicleDB;

internal sealed class MainTransactionHost(ChronicleDatabase database) : ITransactionHost
{
    public bool ReadAt(ReadOnlySpan<byte> key, CommitSequence visibilityBoundary, out byte[] value)
        => database.ReadAt(key, visibilityBoundary, out value);

    public void Commit(Transaction transaction) => database.Commit(transaction);

    public void Abort(Transaction transaction, bool throwIfNotAbortable)
        => database.Abort(transaction, throwIfNotAbortable);

    public void TransactionHandleCompleted() => database.TransactionHandleCompleted();
}

internal sealed class BranchTransactionHost(ChronicleDatabase database, BranchId branchId) : ITransactionHost
{
    public bool ReadAt(ReadOnlySpan<byte> key, CommitSequence visibilityBoundary, out byte[] value)
        => database.ReadBranchAt(branchId, key, visibilityBoundary, out value);

    public void Commit(Transaction transaction) => database.CommitBranch(branchId, transaction);

    public void Abort(Transaction transaction, bool throwIfNotAbortable)
        => database.Abort(transaction, throwIfNotAbortable);

    public void TransactionHandleCompleted() => database.TransactionHandleCompleted();
}
