using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Roots;

/// <summary>
/// Immutable semantic descriptor for one historical retention root.
/// Persistence and crash-safe publication are owned by the composing layer;
/// this type only defines the state that must be persisted and validated.
/// </summary>
public sealed record HistoryRoot(
    HistoryRootId RootId,
    HistoryRootKind Kind,
    Guid OwnerDatabaseId,
    HistoryId HistoryId,
    HistoryId ParentHistoryId,
    CommitSequence Boundary,
    long CreatedUnixMilliseconds,
    HistoryRootState State)
{
    public bool IsRetaining => State is not HistoryRootState.Deleted;

    public HistoryRoot WithState(HistoryRootState state) => this with { State = state };
}
