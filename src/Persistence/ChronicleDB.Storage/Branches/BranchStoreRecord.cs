using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Storage.Branches;

public sealed record BranchStoreRecord(
    BranchStoreRecordType Type,
    ulong EventSequence,
    BranchId BranchId,
    HistoryId HistoryId,
    HistoryId ParentHistoryId,
    HistoryRootId BaseRootId,
    CommitSequence ParentBaseSequence,
    CommitSequence LocalCommitSequence,
    Guid LocalStorageId,
    TransactionId TransactionId,
    int MutationCount,
    long DataLengthAfterCommit,
    long CreatedUnixMilliseconds,
    int Depth,
    string Name);

public sealed record BranchCommitDescriptor(
    TransactionId TransactionId,
    CommitSequence CommitSequence,
    int MutationCount,
    long DataLengthAfterCommit);
