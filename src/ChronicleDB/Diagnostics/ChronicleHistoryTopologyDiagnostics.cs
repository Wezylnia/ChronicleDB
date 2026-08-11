namespace ChronicleDB;

/// <summary>
/// Public, point-in-time description of one independently evolving history domain.
/// These values are observational only and never participate in correctness decisions.
/// </summary>
public sealed record ChronicleHistoryDiagnostics(
    Guid HistoryId,
    string Kind,
    Guid? BranchId,
    string? Name,
    Guid? ParentHistoryId,
    ulong? ParentBaseSequence,
    int Depth,
    ulong CurrentSequence,
    ulong RetentionFloor,
    int LocalCurrentKeyCount,
    int VersionCount,
    int VersionChainCount,
    int MaximumVersionChainLength,
    int SnapshotCount,
    long DataFileBytes,
    long WalFileBytes,
    int OpenRetentionBoundaryCount,
    int OpenBranchHandleCount,
    int ActiveTransactionCount,
    int OpenHistoricalHandleCount);

/// <summary>
/// Explainable persistent retention requirement. OwnerHistoryId identifies the
/// history that owns the root; ProtectedHistoryId identifies the history whose
/// versions the boundary keeps reconstructable.
/// </summary>
public sealed record ChronicleRetentionRootDiagnostics(
    Guid RootId,
    string Kind,
    Guid OwnerHistoryId,
    Guid ProtectedHistoryId,
    ulong Boundary,
    DateTimeOffset CreatedAt,
    string State);

/// <summary>
/// Complete historical-topology snapshot used by the inspector, benchmarks, and
/// research/release validation tooling.
/// </summary>
public sealed record ChronicleHistoryTopologyDiagnostics(
    ChronicleHistoryDiagnostics Main,
    IReadOnlyList<ChronicleHistoryDiagnostics> Branches,
    IReadOnlyList<ChronicleRetentionRootDiagnostics> RetentionRoots);
