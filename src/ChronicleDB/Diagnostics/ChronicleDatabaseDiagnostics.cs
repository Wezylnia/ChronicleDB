namespace ChronicleDB;

/// <summary>
/// Point-in-time observational metrics. Values are intended for diagnostics and research
/// baselines; they are never used to decide correctness or durability.
/// </summary>
public sealed record ChronicleDatabaseDiagnostics(
    Guid DatabaseId,
    DatabaseState State,
    ulong CurrentCommitSequence,
    ulong RetentionFloor,
    int CurrentKeyCount,
    long ActiveTransactions,
    long CommitAttempts,
    long SuccessfulCommits,
    long Aborts,
    long ConflictAborts,
    long CommitSerializationContention,
    double AverageCommitMilliseconds,
    int VersionCount,
    int VersionChainCount,
    double AverageVersionChainLength,
    int MaximumVersionChainLength,
    long IndexContention,
    ulong NextWalLsn,
    long WalFileBytes,
    long WalBytesWrittenThisSession,
    long WalFlushCount,
    double AverageWalFlushMilliseconds,
    long RecoveryReplayedTransactions,
    int SnapshotCount,
    ulong? OldestSnapshotSequence,
    ulong? NewestSnapshotSequence,
    double AverageSnapshotCreateMilliseconds,
    long SnapshotMetadataBytes,
    long DataFileBytes,
    long DataPageCount,
    long OverflowPageCount);
