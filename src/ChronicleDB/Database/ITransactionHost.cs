using ChronicleDB.Core.Sequences;
using ChronicleDB.Transactions;

namespace ChronicleDB;

internal interface ITransactionHost
{
    bool ReadAt(ReadOnlySpan<byte> key, CommitSequence visibilityBoundary, out byte[] value);

    void Commit(Transaction transaction);

    void Abort(Transaction transaction, bool throwIfNotAbortable);

    void TransactionHandleCompleted();
}
