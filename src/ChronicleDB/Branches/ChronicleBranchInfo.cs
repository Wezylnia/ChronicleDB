namespace ChronicleDB;

public sealed record ChronicleBranchInfo(
    Guid BranchId,
    Guid DatabaseId,
    string Name,
    Guid HistoryId,
    Guid ParentHistoryId,
    ulong ParentBaseSequence,
    ulong CurrentSequence,
    int Depth,
    DateTimeOffset CreatedAt);
