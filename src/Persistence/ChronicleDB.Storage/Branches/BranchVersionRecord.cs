using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Storage.Branches;

public sealed record BranchVersionRecord(
    BranchId BranchId,
    HistoryId HistoryId,
    TransactionId TransactionId,
    CommitSequence CommitSequence,
    int MutationIndex,
    int MutationCount,
    byte[] Key,
    bool IsDelete,
    byte[] Value);
