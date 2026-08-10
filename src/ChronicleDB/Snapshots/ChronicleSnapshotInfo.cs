namespace ChronicleDB;

/// <summary>
/// Public immutable description of one retained persistent snapshot.
/// </summary>
public sealed record ChronicleSnapshotInfo(
    Guid SnapshotId,
    Guid DatabaseId,
    string Name,
    ulong Sequence,
    DateTimeOffset CreatedAt);
