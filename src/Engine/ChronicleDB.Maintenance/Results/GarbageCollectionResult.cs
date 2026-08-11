namespace ChronicleDB.Maintenance;

public sealed record GarbageCollectionResult(
    int HistoriesProcessed,
    int VersionsReclaimed,
    long CheckpointBytesWritten,
    ulong MainRetentionFloor,
    int DeletedBranchDirectoriesReclaimed);
