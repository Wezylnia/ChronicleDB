using System.Diagnostics;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Research-only counterfactual oracle for A1. Unlike the production/current
/// retention projection, BranchBase roots are treated as fixed ancestry edges,
/// not as unconditional per-key requirements on the parent history. A child
/// observer propagates to its parent only when that key has no local visible
/// value or tombstone at the observer boundary.
///
/// This type is deliberately observational. It is not consulted by production
/// GC, checkpoint publication, recovery, or compaction.
/// </summary>
public sealed class ShadowAwareRetentionProjection
{
    private const string BranchBaseKind = "BranchBase";

    private readonly ResearchRetentionSnapshot _snapshot;
    private readonly IReadOnlyDictionary<Guid, ResearchHistoryRetentionSnapshot> _histories;
    private readonly Dictionary<Guid, BranchEdge> _parentEdges;
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, ResearchCommittedVersionSnapshot[]>> _versionsByHistoryAndKey;
    private readonly double _constructionMilliseconds;

    public ShadowAwareRetentionProjection(ResearchRetentionSnapshot snapshot)
    {
        var constructionStarted = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        _snapshot = snapshot;
        _histories = snapshot.Histories.ToDictionary(history => history.HistoryId);
        _parentEdges = BuildParentEdges(snapshot);
        _versionsByHistoryAndKey = snapshot.Histories.ToDictionary(
            history => history.HistoryId,
            history => (IReadOnlyDictionary<string, ResearchCommittedVersionSnapshot[]>)history.Versions
                .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(version => version.CommitSequence).ToArray(),
                    StringComparer.Ordinal));
        _constructionMilliseconds = Stopwatch.GetElapsedTime(constructionStarted).TotalMilliseconds;
    }

    public ShadowAwareRetentionProjectionResult Analyze()
    {
        var coreStarted = Stopwatch.GetTimestamp();
        var required = new Dictionary<string, ResearchCommittedVersionSnapshot>(StringComparer.Ordinal);

        // Generic time-travel semantics remain identical to the current exact
        // per-history baseline: every version at/above the floor is observable at
        // some legal boundary, plus the predecessor visible exactly at the floor.
        foreach (var history in _snapshot.Histories)
        {
            AddGenericRequirements(required, history);
        }

        var work = new Queue<ObserverRequirement>();
        var directObserverCount = 0;

        foreach (var history in _snapshot.Histories)
        {
            work.Enqueue(new ObserverRequirement(history.HistoryId, history.RetentionFloor, KeyId: null));
            directObserverCount++;
        }

        foreach (var active in _snapshot.ActiveBoundaries)
        {
            work.Enqueue(new ObserverRequirement(active.ProtectedHistoryId, active.Boundary, KeyId: null));
            directObserverCount++;
        }

        // BranchBase roots describe topology. Other roots (notably persistent
        // snapshots) are direct legal observers and therefore stay authoritative.
        foreach (var root in _snapshot.PersistentRoots.Where(root => !IsBranchBase(root)))
        {
            work.Enqueue(new ObserverRequirement(root.ProtectedHistoryId, root.Boundary, KeyId: null));
            directObserverCount++;
        }

        var allKeyIds = _snapshot.Histories
            .SelectMany(history => history.Versions)
            .Select(version => version.KeyId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Expand boundary-wide observers into per-key requirements. This keeps the
        // recursive resolver simple and makes the key-specific branch shadowing
        // explicit in both implementation and diagnostics.
        var keyedWork = new Queue<ObserverRequirement>();
        while (work.Count > 0)
        {
            var observer = work.Dequeue();
            foreach (var keyId in allKeyIds)
            {
                keyedWork.Enqueue(observer with { KeyId = keyId });
            }
        }

        var visited = new HashSet<ObserverRequirement>();
        var fallbackHops = 0;
        var localShadowStops = 0;
        var rootMissingStops = 0;
        var resolvedObserverKeyCount = 0;

        while (keyedWork.Count > 0)
        {
            var requirement = keyedWork.Dequeue();
            if (!visited.Add(requirement))
            {
                continue;
            }

            resolvedObserverKeyCount++;
            var visible = FindVisibleLocal(requirement.HistoryId, requirement.KeyId!, requirement.Boundary);
            if (visible is not null)
            {
                Add(required, visible);
                localShadowStops++;
                continue;
            }

            if (_parentEdges.TryGetValue(requirement.HistoryId, out var edge))
            {
                fallbackHops++;
                keyedWork.Enqueue(new ObserverRequirement(edge.ParentHistoryId, edge.ParentBoundary, requirement.KeyId));
                continue;
            }

            rootMissingStops++;
        }

        var baseline = CollectBaselineVersions(new RetentionInspector(_snapshot).Context);
        var candidateIds = required.Keys.ToHashSet(StringComparer.Ordinal);
        var baselineIds = baseline.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateIsSubset = candidateIds.IsSubsetOf(baselineIds);
        var releasedIds = baselineIds.Except(candidateIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extraIds = candidateIds.Except(baselineIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var baselinePayload = checked(baseline.Values.Sum(version => version.LogicalPayloadBytes));
        var candidatePayload = Sum(required.Values, version => version.LogicalPayloadBytes);
        var baselineSerialized = checked(baseline.Values.Sum(version => version.SerializedBytes));
        var candidateSerialized = Sum(required.Values, version => version.LogicalSerializedBytes);
        var coreProjectionMilliseconds = Stopwatch.GetElapsedTime(coreStarted).TotalMilliseconds;
        var verificationStarted = Stopwatch.GetTimestamp();
        var equivalence = VerifyObserverEquivalence(required.Keys.ToHashSet(StringComparer.Ordinal), allKeyIds);
        var observerVerificationMilliseconds = Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds;

        return new ShadowAwareRetentionProjectionResult(
            BaselineVersionCount: baseline.Count,
            ShadowAwareVersionCount: required.Count,
            BaselinePayloadBytes: baselinePayload,
            ShadowAwarePayloadBytes: candidatePayload,
            ShadowReleasedPayloadBytes: checked(baselinePayload - candidatePayload),
            BaselineSerializedBytes: baselineSerialized,
            ShadowAwareSerializedBytes: candidateSerialized,
            ShadowReleasedSerializedBytes: checked(baselineSerialized - candidateSerialized),
            ShadowAwareReclamationRatio: candidatePayload == 0
                ? (baselinePayload == 0 ? 1d : double.PositiveInfinity)
                : (double)baselinePayload / candidatePayload,
            CandidateIsSubsetOfBaseline: candidateIsSubset,
            ReleasedVersionIds: Array.AsReadOnly(releasedIds),
            ExtraVersionIds: Array.AsReadOnly(extraIds),
            RequiredVersionIds: Array.AsReadOnly(required.Keys.Order(StringComparer.Ordinal).ToArray()),
            DirectObserverCount: directObserverCount,
            ObserverKeyResolutionCount: resolvedObserverKeyCount,
            ParentFallbackHops: fallbackHops,
            LocalShadowStops: localShadowStops,
            RootMissingStops: rootMissingStops,
            ObserverEquivalenceVerified: equivalence.Mismatches.Count == 0,
            ObserverEquivalenceCheckCount: equivalence.CheckCount,
            ObserverMismatches: equivalence.Mismatches,
            ObserverMinimalityVerified: equivalence.UnwitnessedRequiredVersionIds.Count == 0,
            UnwitnessedRequiredVersionIds: equivalence.UnwitnessedRequiredVersionIds,
            ConstructionMilliseconds: _constructionMilliseconds,
            CoreProjectionMilliseconds: coreProjectionMilliseconds,
            ObserverVerificationMilliseconds: observerVerificationMilliseconds);
    }

    private ObserverEquivalenceResult VerifyObserverEquivalence(
        IReadOnlySet<string> retainedVersionIds,
        IReadOnlyList<string> allKeyIds)
    {
        var mismatches = new List<ShadowAwareObserverMismatch>();
        var witnessedVersionIds = new HashSet<string>(StringComparer.Ordinal);
        var checkCount = 0;
        foreach (var observer in EnumerateLegalObservers())
        {
            foreach (var keyId in allKeyIds)
            {
                checkCount++;
                var original = ResolveObserver(observer.HistoryId, observer.Boundary, keyId, retainedVersionIds: null);
                var projected = ResolveObserver(observer.HistoryId, observer.Boundary, keyId, retainedVersionIds);
                if (original is not null)
                {
                    witnessedVersionIds.Add(original.VersionId);
                }

                if (!string.Equals(original?.VersionId, projected?.VersionId, StringComparison.Ordinal))
                {
                    mismatches.Add(new ShadowAwareObserverMismatch(
                        observer.HistoryId,
                        observer.Boundary,
                        keyId,
                        original?.VersionId,
                        projected?.VersionId));
                }
            }
        }

        var unwitnessed = retainedVersionIds
            .Except(witnessedVersionIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ObserverEquivalenceResult(
            checkCount,
            Array.AsReadOnly(mismatches.ToArray()),
            Array.AsReadOnly(unwitnessed));
    }

    private BoundaryObserver[] EnumerateLegalObservers()
    {
        var observers = new HashSet<BoundaryObserver>();
        foreach (var history in _snapshot.Histories)
        {
            observers.Add(new BoundaryObserver(history.HistoryId, history.RetentionFloor));
            observers.Add(new BoundaryObserver(history.HistoryId, history.CurrentSequence));
            foreach (var version in history.Versions.Where(version => version.CommitSequence >= history.RetentionFloor))
            {
                observers.Add(new BoundaryObserver(history.HistoryId, version.CommitSequence));
            }
        }

        foreach (var active in _snapshot.ActiveBoundaries)
        {
            observers.Add(new BoundaryObserver(active.ProtectedHistoryId, active.Boundary));
        }

        foreach (var root in _snapshot.PersistentRoots.Where(root => !IsBranchBase(root)))
        {
            observers.Add(new BoundaryObserver(root.ProtectedHistoryId, root.Boundary));
        }

        return observers
            .OrderBy(observer => observer.HistoryId)
            .ThenBy(observer => observer.Boundary)
            .ToArray();
    }

    private ResearchCommittedVersionSnapshot? ResolveObserver(
        Guid historyId,
        ulong boundary,
        string keyId,
        IReadOnlySet<string>? retainedVersionIds)
    {
        var currentHistory = historyId;
        var currentBoundary = boundary;
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(currentHistory))
            {
                throw new InvalidOperationException("Branch ancestry became cyclic during observer verification.");
            }

            var local = FindVisibleLocal(currentHistory, keyId, currentBoundary, retainedVersionIds);
            if (local is not null)
            {
                return local;
            }

            if (!_parentEdges.TryGetValue(currentHistory, out var edge))
            {
                return null;
            }

            currentHistory = edge.ParentHistoryId;
            currentBoundary = edge.ParentBoundary;
        }
    }

    private ResearchCommittedVersionSnapshot? FindVisibleLocal(
        Guid historyId,
        string keyId,
        ulong boundary,
        IReadOnlySet<string>? retainedVersionIds = null)
    {
        if (!_versionsByHistoryAndKey.TryGetValue(historyId, out var byKey)
            || !byKey.TryGetValue(keyId, out var versions))
        {
            return null;
        }

        // Histories are small in the research oracle. Reverse linear search keeps
        // the reference implementation obvious and independent from engine indexes.
        for (var index = versions.Length - 1; index >= 0; index--)
        {
            if (versions[index].CommitSequence <= boundary
                && (retainedVersionIds is null || retainedVersionIds.Contains(versions[index].VersionId)))
            {
                return versions[index];
            }
        }

        return null;
    }

    private static Dictionary<Guid, BranchEdge> BuildParentEdges(ResearchRetentionSnapshot snapshot)
    {
        var result = new Dictionary<Guid, BranchEdge>();
        foreach (var root in snapshot.PersistentRoots.Where(IsBranchBase))
        {
            if (result.TryGetValue(root.OwnerHistoryId, out var existing)
                && (existing.ParentHistoryId != root.ProtectedHistoryId || existing.ParentBoundary != root.Boundary))
            {
                throw new ArgumentException(
                    $"History '{root.OwnerHistoryId}' has conflicting BranchBase roots.",
                    nameof(snapshot));
            }

            result[root.OwnerHistoryId] = new BranchEdge(root.ProtectedHistoryId, root.Boundary);
        }

        return result;
    }

    private static bool IsBranchBase(ResearchPersistentRetentionRootSnapshot root)
        => root.Kind.Equals(BranchBaseKind, StringComparison.Ordinal);

    private static void AddGenericRequirements(
        IDictionary<string, ResearchCommittedVersionSnapshot> target,
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

        foreach (var visible in VisiblePredecessors(history.Versions, history.RetentionFloor))
        {
            Add(target, visible);
        }

        foreach (var newest in history.Versions
                     .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                     .Select(group => group.OrderBy(version => version.CommitSequence).Last()))
        {
            Add(target, newest);
        }
    }

    private static IEnumerable<ResearchCommittedVersionSnapshot> VisiblePredecessors(
        IReadOnlyList<ResearchCommittedVersionSnapshot> versions,
        ulong boundary)
        => versions
            .Where(version => version.CommitSequence <= boundary)
            .GroupBy(version => version.KeyId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(version => version.CommitSequence).Last());

    private static Dictionary<string, RetentionVersion> CollectBaselineVersions(RetentionContext context)
    {
        var result = new Dictionary<string, RetentionVersion>(StringComparer.Ordinal);
        foreach (var version in context.GloballyRequiredVersions.Concat(context.Roots.SelectMany(root => root.RequiredVersions)))
        {
            result[version.VersionId] = version;
        }

        return result;
    }

    private static void Add(
        IDictionary<string, ResearchCommittedVersionSnapshot> target,
        ResearchCommittedVersionSnapshot version)
        => target[version.VersionId] = version;

    private static long Sum(
        IEnumerable<ResearchCommittedVersionSnapshot> versions,
        Func<ResearchCommittedVersionSnapshot, long> selector)
        => checked(versions.Sum(selector));

    private static void Validate(ResearchRetentionSnapshot snapshot)
    {
        if (snapshot.Histories.GroupBy(history => history.HistoryId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("History IDs must be unique.", nameof(snapshot));
        }

        var histories = snapshot.Histories.ToDictionary(history => history.HistoryId);
        foreach (var history in snapshot.Histories)
        {
            if (history.RetentionFloor > history.CurrentSequence)
            {
                throw new ArgumentException("A history retention floor cannot exceed its current sequence.", nameof(snapshot));
            }

            if (history.Versions.Any(version => version.CommitSequence > history.CurrentSequence))
            {
                throw new ArgumentException("A captured version cannot exceed its history current sequence.", nameof(snapshot));
            }
        }

        foreach (var root in snapshot.PersistentRoots)
        {
            if (!histories.ContainsKey(root.ProtectedHistoryId))
            {
                throw new ArgumentException("Every root must reference a captured protected history.", nameof(snapshot));
            }

            if (IsBranchBase(root))
            {
                if (!histories.ContainsKey(root.OwnerHistoryId))
                {
                    throw new ArgumentException("Every BranchBase owner must be a captured history.", nameof(snapshot));
                }

                if (root.OwnerHistoryId == root.ProtectedHistoryId)
                {
                    throw new ArgumentException("A BranchBase edge cannot point a history to itself.", nameof(snapshot));
                }
            }
        }

        if (snapshot.ActiveBoundaries.Any(boundary => !histories.ContainsKey(boundary.ProtectedHistoryId)))
        {
            throw new ArgumentException("Every active boundary must reference a captured history.", nameof(snapshot));
        }

        var parentEdges = BuildParentEdges(snapshot);
        foreach (var historyId in histories.Keys)
        {
            var seen = new HashSet<Guid>();
            var current = historyId;
            while (parentEdges.TryGetValue(current, out var edge))
            {
                if (!seen.Add(current))
                {
                    throw new ArgumentException("BranchBase roots must form an acyclic history forest.", nameof(snapshot));
                }

                current = edge.ParentHistoryId;
            }
        }
    }

    private sealed record ObserverEquivalenceResult(
        int CheckCount,
        IReadOnlyList<ShadowAwareObserverMismatch> Mismatches,
        IReadOnlyList<string> UnwitnessedRequiredVersionIds);

    private readonly record struct BoundaryObserver(Guid HistoryId, ulong Boundary);

    private readonly record struct BranchEdge(Guid ParentHistoryId, ulong ParentBoundary);

    private readonly record struct ObserverRequirement(Guid HistoryId, ulong Boundary, string? KeyId);
}

public sealed record ShadowAwareRetentionProjectionResult(
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long BaselinePayloadBytes,
    long ShadowAwarePayloadBytes,
    long ShadowReleasedPayloadBytes,
    long BaselineSerializedBytes,
    long ShadowAwareSerializedBytes,
    long ShadowReleasedSerializedBytes,
    double ShadowAwareReclamationRatio,
    bool CandidateIsSubsetOfBaseline,
    IReadOnlyList<string> ReleasedVersionIds,
    IReadOnlyList<string> ExtraVersionIds,
    IReadOnlyList<string> RequiredVersionIds,
    int DirectObserverCount,
    int ObserverKeyResolutionCount,
    int ParentFallbackHops,
    int LocalShadowStops,
    int RootMissingStops,
    bool ObserverEquivalenceVerified,
    int ObserverEquivalenceCheckCount,
    IReadOnlyList<ShadowAwareObserverMismatch> ObserverMismatches,
    bool ObserverMinimalityVerified,
    IReadOnlyList<string> UnwitnessedRequiredVersionIds,
    double ConstructionMilliseconds,
    double CoreProjectionMilliseconds,
    double ObserverVerificationMilliseconds);

public sealed record ShadowAwareObserverMismatch(
    Guid HistoryId,
    ulong Boundary,
    string KeyId,
    string? OriginalVersionId,
    string? ProjectedVersionId);
