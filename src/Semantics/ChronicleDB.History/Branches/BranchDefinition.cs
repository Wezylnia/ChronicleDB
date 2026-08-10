using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Branches;

/// <summary>
/// Immutable semantic description of one active writable history domain.
/// The parent base sequence is expressed in <see cref="ParentHistoryId"/>;
/// <see cref="LocalCurrentSequence"/> is expressed in <see cref="HistoryId"/>.
/// </summary>
public sealed record BranchDefinition(
    BranchId BranchId,
    string Name,
    Guid OwnerDatabaseId,
    HistoryId HistoryId,
    HistoryId ParentHistoryId,
    HistoryRootId BaseRootId,
    CommitSequence ParentBaseSequence,
    CommitSequence LocalCurrentSequence,
    Guid LocalStorageId,
    long CreatedUnixMilliseconds,
    int Depth,
    BranchLifecycleState State)
{
    public BranchDefinition WithCurrentSequence(CommitSequence sequence)
        => this with { LocalCurrentSequence = sequence };
}
