namespace ChronicleDB.Diagnostics.Research;

public enum ErasureObserverContractKind : byte
{
    GenericTimeTravel = 0,
    CurrentState = 1,
    PersistentSnapshot = 2,
    ActiveBoundary = 3,
}

public sealed record ObserverExactErasureWitness(
    string ObserverId,
    ErasureObserverContractKind Kind,
    Guid HistoryId,
    ulong Boundary,
    ErasureContentState Content,
    string? ResolvedVersionId,
    Guid? ResolvedHistoryId,
    ulong? ResolvedSequence,
    int ParentFallbackHops)
{
    public bool ReconstructsValue => Content == ErasureContentState.Value;
}

public sealed record LegacyErasureRootClassification(
    Guid RootId,
    string Kind,
    Guid OwnerHistoryId,
    Guid ProtectedHistoryId,
    ulong Boundary,
    ErasureContentState Content,
    string? ResolvedVersionId)
{
    public bool ReconstructsValue => Content == ErasureContentState.Value;
}

public sealed record ObserverExactErasureOracleResult(
    string KeyId,
    IReadOnlyList<ObserverExactErasureWitness> Observers,
    IReadOnlyList<ObserverExactErasureWitness> BlockingObservers,
    IReadOnlyList<ObserverExactErasureWitness> InheritedBlockingObservers,
    IReadOnlyList<LegacyErasureRootClassification> LegacyLocalRootClassifications,
    IReadOnlyList<LegacyErasureRootClassification> LegacyLocalRootBlockers,
    IReadOnlyList<string> BlockingObserverIdsUnrepresentedByLegacyP6,
    IReadOnlyList<Guid> BranchBaseFalsePositiveRootIds)
{
    public int ObserverCount => Observers.Count;
    public int BlockingObserverCount => BlockingObservers.Count;
    public int InheritedBlockingObserverCount => InheritedBlockingObservers.Count;
}

/// <summary>
/// Research-only semantic oracle for A8. It computes the key-specific set of legal
/// retained observers that can reconstruct a value under ChronicleDB branch-read
/// semantics. BranchBase roots are ancestry edges, not unconditional per-key
/// blockers. A local value or tombstone stops fallback; only absence falls back to
/// the parent's fixed historical boundary.
///
/// This type is observational and independent of production erasure, GC, recovery,
/// checkpoint publication, and the frozen A1 projection implementation.
/// </summary>
public sealed class ObserverExactErasureOracle
{
    private const string BranchBaseKind = "BranchBase";

    private readonly ResearchRetentionSnapshot _snapshot;
    private readonly Dictionary<Guid, ResearchHistoryRetentionSnapshot> _histories;
    private readonly Dictionary<Guid, BranchEdge> _parentEdges;
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, ResearchCommittedVersionSnapshot[]>> _versionsByHistoryAndKey;

    public ObserverExactErasureOracle(ResearchRetentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        _snapshot = snapshot;
        _histories = snapshot.Histories.ToDictionary(history => history.HistoryId);
        _parentEdges = BuildParentEdges(snapshot);
        ValidateAcyclic(_histories.Keys, _parentEdges);
        _versionsByHistoryAndKey = snapshot.Histories.ToDictionary(
            history => history.HistoryId,
            history => (IReadOnlyDictionary<string, ResearchCommittedVersionSnapshot[]>)history.Versions
                .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(version => version.CommitSequence).ToArray(),
                    StringComparer.Ordinal));
    }

    public ObserverExactErasureOracleResult Analyze(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("Observer-exact erasure analysis requires a stable key ID.", nameof(keyId));
        }

        var observers = EnumerateObservers(keyId)
            .Select(observer => Resolve(observer, keyId))
            .OrderBy(observer => observer.HistoryId)
            .ThenBy(observer => observer.Boundary)
            .ThenBy(observer => observer.Kind)
            .ThenBy(observer => observer.ObserverId, StringComparer.Ordinal)
            .ToArray();
        var blockers = observers.Where(observer => observer.ReconstructsValue).ToArray();
        var inherited = blockers.Where(observer => observer.ParentFallbackHops > 0).ToArray();

        var legacy = ClassifyLegacyRoots(keyId);
        var legacyBlockers = legacy.Where(item => item.ReconstructsValue).ToArray();
        var representedBlockingObserverIds = legacyBlockers
            .Where(item => !IsBranchBase(item.Kind))
            .Select(item => RootObserverId(item.RootId))
            .ToHashSet(StringComparer.Ordinal);
        var unrepresented = blockers
            .Select(observer => observer.ObserverId)
            .Where(observerId => !representedBlockingObserverIds.Contains(observerId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var branchBaseFalsePositives = legacyBlockers
            .Where(item => IsBranchBase(item.Kind))
            .Where(item => !BranchEdgeIsNeededByAnyBlockingObserver(item.OwnerHistoryId, item.ProtectedHistoryId, blockers))
            .Select(item => item.RootId)
            .Order()
            .ToArray();

        return new ObserverExactErasureOracleResult(
            keyId,
            Array.AsReadOnly(observers),
            Array.AsReadOnly(blockers),
            Array.AsReadOnly(inherited),
            Array.AsReadOnly(legacy),
            Array.AsReadOnly(legacyBlockers),
            Array.AsReadOnly(unrepresented),
            Array.AsReadOnly(branchBaseFalsePositives));
    }

    private BoundaryObserver[] EnumerateObservers(string keyId)
    {
        var observers = new Dictionary<ObserverBoundaryIdentity, BoundaryObserver>();
        foreach (var history in _snapshot.Histories)
        {
            AddGenericObserver(observers, history, history.RetentionFloor, keyId);
            foreach (var version in history.Versions.Where(version =>
                         version.KeyId.Equals(keyId, StringComparison.Ordinal)
                         && version.CommitSequence >= history.RetentionFloor))
            {
                AddGenericObserver(observers, history, version.CommitSequence, keyId);
            }
            AddGenericObserver(observers, history, history.CurrentSequence, keyId);
            AddCurrentObserver(observers, history, keyId);
        }

        foreach (var active in _snapshot.ActiveBoundaries)
        {
            var id = $"active:{active.ProtectedHistoryId:N}:{active.Boundary}";
            observers[new ObserverBoundaryIdentity(id, active.ProtectedHistoryId, active.Boundary)] = new BoundaryObserver(
                id,
                ErasureObserverContractKind.ActiveBoundary,
                active.ProtectedHistoryId,
                active.Boundary);
        }

        foreach (var root in _snapshot.PersistentRoots.Where(root => !IsBranchBase(root.Kind)))
        {
            var id = RootObserverId(root.RootId);
            observers[new ObserverBoundaryIdentity(id, root.ProtectedHistoryId, root.Boundary)] = new BoundaryObserver(
                id,
                ErasureObserverContractKind.PersistentSnapshot,
                root.ProtectedHistoryId,
                root.Boundary);
        }

        return observers.Values.ToArray();
    }

    private static void AddGenericObserver(
        Dictionary<ObserverBoundaryIdentity, BoundaryObserver> target,
        ResearchHistoryRetentionSnapshot history,
        ulong boundary,
        string keyId)
    {
        var id = $"generic:{history.HistoryId:N}:{boundary}:{keyId}";
        target[new ObserverBoundaryIdentity(id, history.HistoryId, boundary)] = new BoundaryObserver(
            id,
            ErasureObserverContractKind.GenericTimeTravel,
            history.HistoryId,
            boundary);
    }

    private static void AddCurrentObserver(
        Dictionary<ObserverBoundaryIdentity, BoundaryObserver> target,
        ResearchHistoryRetentionSnapshot history,
        string keyId)
    {
        var id = $"current:{history.HistoryId:N}:{keyId}";
        target[new ObserverBoundaryIdentity(id, history.HistoryId, history.CurrentSequence)] = new BoundaryObserver(
            id,
            ErasureObserverContractKind.CurrentState,
            history.HistoryId,
            history.CurrentSequence);
    }

    private ObserverExactErasureWitness Resolve(BoundaryObserver observer, string keyId)
    {
        var historyId = observer.HistoryId;
        var boundary = observer.Boundary;
        for (var hops = 0; hops <= _histories.Count; hops++)
        {
            var local = FindVisibleLocal(historyId, keyId, boundary);
            if (local is not null)
            {
                return new ObserverExactErasureWitness(
                    observer.ObserverId,
                    observer.Kind,
                    observer.HistoryId,
                    observer.Boundary,
                    local.IsTombstone ? ErasureContentState.Tombstone : ErasureContentState.Value,
                    local.VersionId,
                    historyId,
                    local.CommitSequence,
                    hops);
            }

            if (!_parentEdges.TryGetValue(historyId, out var edge))
            {
                return new ObserverExactErasureWitness(
                    observer.ObserverId,
                    observer.Kind,
                    observer.HistoryId,
                    observer.Boundary,
                    ErasureContentState.Absent,
                    null,
                    null,
                    null,
                    hops);
            }

            historyId = edge.ParentHistoryId;
            boundary = edge.ParentBoundary;
        }

        throw new InvalidOperationException("Erasure observer resolution exceeded the captured history count.");
    }

    private LegacyErasureRootClassification[] ClassifyLegacyRoots(string keyId)
        => _snapshot.PersistentRoots
            .OrderBy(root => root.RootId)
            .Select(root =>
            {
                var visible = FindVisibleLocal(root.ProtectedHistoryId, keyId, root.Boundary);
                return new LegacyErasureRootClassification(
                    root.RootId,
                    root.Kind,
                    root.OwnerHistoryId,
                    root.ProtectedHistoryId,
                    root.Boundary,
                    visible is null
                        ? ErasureContentState.Absent
                        : visible.IsTombstone ? ErasureContentState.Tombstone : ErasureContentState.Value,
                    visible?.VersionId);
            })
            .ToArray();

    private ResearchCommittedVersionSnapshot? FindVisibleLocal(Guid historyId, string keyId, ulong boundary)
    {
        if (!_versionsByHistoryAndKey.TryGetValue(historyId, out var byKey)
            || !byKey.TryGetValue(keyId, out var versions))
        {
            return null;
        }

        var low = 0;
        var high = versions.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (versions[middle].CommitSequence <= boundary)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate >= 0 ? versions[candidate] : null;
    }

    private bool BranchEdgeIsNeededByAnyBlockingObserver(
        Guid childHistoryId,
        Guid parentHistoryId,
        IReadOnlyList<ObserverExactErasureWitness> blockers)
    {
        foreach (var blocker in blockers.Where(item => item.ParentFallbackHops > 0))
        {
            var history = blocker.HistoryId;
            for (var hops = 0; hops < blocker.ParentFallbackHops; hops++)
            {
                if (!_parentEdges.TryGetValue(history, out var edge))
                {
                    break;
                }

                if (history == childHistoryId && edge.ParentHistoryId == parentHistoryId)
                {
                    return true;
                }
                history = edge.ParentHistoryId;
            }
        }
        return false;
    }

    private static Dictionary<Guid, BranchEdge> BuildParentEdges(ResearchRetentionSnapshot snapshot)
    {
        var edges = new Dictionary<Guid, BranchEdge>();
        foreach (var root in snapshot.PersistentRoots.Where(root => IsBranchBase(root.Kind)))
        {
            var edge = new BranchEdge(root.ProtectedHistoryId, root.Boundary);
            if (edges.TryGetValue(root.OwnerHistoryId, out var existing) && existing != edge)
            {
                throw new ArgumentException($"History '{root.OwnerHistoryId}' has conflicting BranchBase roots.", nameof(snapshot));
            }
            edges[root.OwnerHistoryId] = edge;
        }
        return edges;
    }

    private static void ValidateAcyclic(
        IEnumerable<Guid> historyIds,
        IReadOnlyDictionary<Guid, BranchEdge> edges)
    {
        foreach (var origin in historyIds)
        {
            var seen = new HashSet<Guid>();
            var current = origin;
            while (edges.TryGetValue(current, out var edge))
            {
                if (!seen.Add(current))
                {
                    throw new ArgumentException("Erasure observer topology must be acyclic.", nameof(edges));
                }
                current = edge.ParentHistoryId;
            }
        }
    }

    private static void Validate(ResearchRetentionSnapshot snapshot)
    {
        if (snapshot.Histories.Count == 0)
        {
            throw new ArgumentException("Erasure observer analysis requires at least one history.", nameof(snapshot));
        }

        if (snapshot.Histories.Any(history => history.HistoryId == Guid.Empty)
            || snapshot.Histories.GroupBy(history => history.HistoryId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("History IDs must be unique and valid.", nameof(snapshot));
        }

        var historyIds = snapshot.Histories.Select(history => history.HistoryId).ToHashSet();
        var versionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var history in snapshot.Histories)
        {
            if (history.RetentionFloor > history.CurrentSequence)
            {
                throw new ArgumentException("History retention floor cannot exceed current sequence.", nameof(snapshot));
            }

            foreach (var version in history.Versions)
            {
                if (string.IsNullOrWhiteSpace(version.VersionId)
                    || string.IsNullOrWhiteSpace(version.KeyId)
                    || version.CommitSequence > history.CurrentSequence
                    || !versionIds.Add(version.VersionId))
                {
                    throw new ArgumentException("Captured versions must have unique valid identities within committed history.", nameof(snapshot));
                }
            }
        }

        foreach (var root in snapshot.PersistentRoots)
        {
            if (root.RootId == Guid.Empty
                || !historyIds.Contains(root.OwnerHistoryId)
                || !historyIds.Contains(root.ProtectedHistoryId)
                || root.Boundary > snapshot.GetHistory(root.ProtectedHistoryId).CurrentSequence)
            {
                throw new ArgumentException("Persistent roots must reference valid captured histories and boundaries.", nameof(snapshot));
            }
        }

        foreach (var active in snapshot.ActiveBoundaries)
        {
            if (!historyIds.Contains(active.ProtectedHistoryId)
                || active.Boundary > snapshot.GetHistory(active.ProtectedHistoryId).CurrentSequence)
            {
                throw new ArgumentException("Active boundaries must reference valid captured history.", nameof(snapshot));
            }
        }
    }

    private static bool IsBranchBase(string kind)
        => kind.Equals(BranchBaseKind, StringComparison.Ordinal);

    private static string RootObserverId(Guid rootId) => $"root:{rootId:N}";

    private readonly record struct BranchEdge(Guid ParentHistoryId, ulong ParentBoundary);
    private readonly record struct BoundaryObserver(
        string ObserverId,
        ErasureObserverContractKind Kind,
        Guid HistoryId,
        ulong Boundary);
    private readonly record struct ObserverBoundaryIdentity(string ObserverId, Guid HistoryId, ulong Boundary);
}
