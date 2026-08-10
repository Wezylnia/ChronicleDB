namespace ChronicleDB;

public sealed record ChronicleBranchSnapshotInfo(
    Guid SnapshotId,
    Guid BranchId,
    Guid HistoryId,
    string Name,
    ulong Sequence,
    DateTimeOffset CreatedAt);
