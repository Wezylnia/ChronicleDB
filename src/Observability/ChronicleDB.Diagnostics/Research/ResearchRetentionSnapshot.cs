namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Read-only raw MVCC retention snapshot captured from ChronicleDB for research analysis.
/// It is observational data only and is never consulted by reclamation correctness paths.
/// </summary>
public sealed record ResearchRetentionSnapshot(
    IReadOnlyList<ResearchHistoryRetentionSnapshot> Histories,
    IReadOnlyList<ResearchPersistentRetentionRootSnapshot> PersistentRoots,
    IReadOnlyList<ResearchActiveRetentionBoundarySnapshot> ActiveBoundaries)
{
    public ResearchHistoryRetentionSnapshot GetHistory(Guid historyId)
        => Histories.Single(history => history.HistoryId == historyId);
}

public sealed record ResearchHistoryRetentionSnapshot(
    Guid HistoryId,
    ulong RetentionFloor,
    ulong CurrentSequence,
    IReadOnlyList<ResearchCommittedVersionSnapshot> Versions);

public sealed record ResearchCommittedVersionSnapshot(
    string VersionId,
    Guid TransactionId,
    ulong CommitSequence,
    string KeyId,
    int KeyBytes,
    int ValueBytes,
    bool IsTombstone)
{
    public long LogicalPayloadBytes => IsTombstone ? 0L : ValueBytes;

    // Deliberately only a deterministic logical serialization lower bound. It is not
    // presented as on-disk/checkpoint bytes; physical accounting is measured separately.
    public long LogicalSerializedBytes => checked((long)KeyBytes + ValueBytes);
}

public sealed record ResearchPersistentRetentionRootSnapshot(
    Guid RootId,
    string Kind,
    Guid OwnerHistoryId,
    Guid ProtectedHistoryId,
    ulong Boundary);

public sealed record ResearchActiveRetentionBoundarySnapshot(
    Guid ProtectedHistoryId,
    ulong Boundary);

public sealed record RetentionRootExplanation(
    string RootId,
    Guid ProtectedHistoryId,
    ulong Boundary,
    IReadOnlyList<string> RequiredVersionIds,
    RetentionAnalysisResult CounterfactualDrop);

/// <summary>
/// Independent observer-exact reference oracle used by P1. It reconstructs the
/// retained logical version set from raw history, generic floors, process-local
/// observer boundaries, and explicit persistent roots. It does not call the
/// engine's production retention projection and therefore can serve as a
/// differential oracle for that implementation.
/// </summary>
public sealed class RetentionInspector
{
    private readonly ResearchRetentionSnapshot _snapshot;
    private readonly RetentionContext _context;
    private readonly Dictionary<string, RetentionRoot> _roots;
    private readonly Dictionary<string, ResearchPersistentRetentionRootSnapshot> _rootMetadata;

    public RetentionInspector(ResearchRetentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        _snapshot = snapshot;

        var global = new Dictionary<string, RetentionVersion>(StringComparer.Ordinal);
        foreach (var history in snapshot.Histories)
        {
            AddGenericRequirements(global, history);
            foreach (var active in snapshot.ActiveBoundaries.Where(item => item.ProtectedHistoryId == history.HistoryId))
            {
                AddVisiblePredecessors(global, history.Versions, active.Boundary);
            }
        }

        var roots = new List<RetentionRoot>(snapshot.PersistentRoots.Count);
        var metadata = new Dictionary<string, ResearchPersistentRetentionRootSnapshot>(StringComparer.Ordinal);
        foreach (var root in snapshot.PersistentRoots)
        {
            var rootId = root.RootId.ToString("N");
            var history = snapshot.GetHistory(root.ProtectedHistoryId);
            var required = new Dictionary<string, RetentionVersion>(StringComparer.Ordinal);
            AddVisiblePredecessors(required, history.Versions, root.Boundary);
            roots.Add(new RetentionRoot(rootId, required.Values));
            metadata.Add(rootId, root);
        }

        _context = new RetentionContext(global.Values, roots);
        _roots = roots.ToDictionary(root => root.RootId, StringComparer.Ordinal);
        _rootMetadata = metadata;
    }

    public RetentionContext Context => _context;

    public RetentionRootExplanation ExplainRetention(Guid rootId)
    {
        var id = rootId.ToString("N");
        if (!_roots.TryGetValue(id, out var root) || !_rootMetadata.TryGetValue(id, out var metadata))
        {
            throw new ArgumentException($"Unknown retention root '{rootId}'.", nameof(rootId));
        }

        return new RetentionRootExplanation(
            id,
            metadata.ProtectedHistoryId,
            metadata.Boundary,
            root.RequiredVersions.Select(version => version.VersionId).Order(StringComparer.Ordinal).ToArray(),
            MarginalRetentionAnalyzer.Analyze(_context, [id]));
    }

    public RetentionAnalysisResult WhatIfDrop(Guid rootId)
        => WhatIfDrop([rootId]);

    public RetentionAnalysisResult WhatIfDrop(IEnumerable<Guid> rootIds)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        var ids = rootIds.Select(rootId => rootId.ToString("N")).ToArray();
        return MarginalRetentionAnalyzer.Analyze(_context, ids);
    }

    private static void AddGenericRequirements(
        IDictionary<string, RetentionVersion> target,
        ResearchHistoryRetentionSnapshot history)
    {
        if (history.Versions.Count == 0)
        {
            return;
        }

        foreach (var version in history.Versions.Where(version => version.CommitSequence >= history.RetentionFloor))
        {
            Add(target, version);
        }

        AddVisiblePredecessors(target, history.Versions, history.RetentionFloor);

        foreach (var newest in history.Versions
                     .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                     .Select(group => group.OrderBy(version => version.CommitSequence).Last()))
        {
            Add(target, newest);
        }
    }

    private static void AddVisiblePredecessors(
        IDictionary<string, RetentionVersion> target,
        IReadOnlyList<ResearchCommittedVersionSnapshot> versions,
        ulong boundary)
    {
        foreach (var visible in versions
                     .Where(version => version.CommitSequence <= boundary)
                     .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                     .Select(group => group.OrderBy(version => version.CommitSequence).Last()))
        {
            Add(target, visible);
        }
    }

    private static void Add(
        IDictionary<string, RetentionVersion> target,
        ResearchCommittedVersionSnapshot version)
    {
        target[version.VersionId] = new RetentionVersion(
            version.VersionId,
            version.LogicalPayloadBytes,
            version.LogicalSerializedBytes,
            version.IsTombstone);
    }

    private static void ValidateSnapshot(ResearchRetentionSnapshot snapshot)
    {
        if (snapshot.Histories.GroupBy(history => history.HistoryId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("History IDs must be unique.", nameof(snapshot));
        }

        var historyIds = snapshot.Histories.Select(history => history.HistoryId).ToHashSet();
        if (snapshot.PersistentRoots.Any(root => !historyIds.Contains(root.ProtectedHistoryId))
            || snapshot.ActiveBoundaries.Any(root => !historyIds.Contains(root.ProtectedHistoryId)))
        {
            throw new ArgumentException("Every retention boundary must reference a captured history.", nameof(snapshot));
        }

        foreach (var history in snapshot.Histories)
        {
            if (history.Versions.GroupBy(version => version.VersionId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            {
                throw new ArgumentException("Version IDs must be unique within one history.", nameof(snapshot));
            }
        }
    }
}
