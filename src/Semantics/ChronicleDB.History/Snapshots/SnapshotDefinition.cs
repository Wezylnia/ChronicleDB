using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Snapshots;

/// <summary>
/// Immutable semantic description of one retained persistent snapshot.
/// Persistence is owned by the storage layer and coordinated by the facade.
/// </summary>
public sealed record SnapshotDefinition(
    SnapshotId SnapshotId,
    string Name,
    CommitSequence Sequence,
    long CreatedUnixMilliseconds);
