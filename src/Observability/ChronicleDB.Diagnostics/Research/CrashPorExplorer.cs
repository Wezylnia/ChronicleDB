using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// One abstract durable action in the bounded P2 model. This is deliberately
/// independent from the production recovery implementation so exhaustive/POR
/// comparison can act as a research oracle rather than another execution path.
/// </summary>
public sealed record PersistenceAction
{
    public PersistenceAction(
        long actionId,
        ResearchEventKind eventKind,
        HistoryId historyId,
        HistoryId? parentHistoryId,
        Guid operationId,
        IEnumerable<string> resourceSet,
        ResearchDurabilityPhase durabilityPhase,
        ulong authorityGeneration,
        IEnumerable<long>? dependencyActionIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actionId);
        if (!historyId.IsValid)
        {
            throw new ArgumentException("Persistence actions require a valid history ID.", nameof(historyId));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Persistence actions require an operation ID.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(resourceSet);
        var resources = resourceSet.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (resources.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Resource IDs cannot be blank.", nameof(resourceSet));
        }

        var dependencies = (dependencyActionIds ?? [])
            .Distinct()
            .Order()
            .ToArray();
        if (dependencies.Any(id => id <= 0 || id == actionId))
        {
            throw new ArgumentException("Dependency IDs must be positive and cannot self-reference.", nameof(dependencyActionIds));
        }

        ActionId = actionId;
        EventKind = eventKind;
        HistoryId = historyId;
        ParentHistoryId = parentHistoryId;
        OperationId = operationId;
        ResourceSet = Array.AsReadOnly(resources);
        DurabilityPhase = durabilityPhase;
        AuthorityGeneration = authorityGeneration;
        DependencyActionIds = Array.AsReadOnly(dependencies);
    }

    public long ActionId { get; }
    public ResearchEventKind EventKind { get; }
    public HistoryId HistoryId { get; }
    public HistoryId? ParentHistoryId { get; }
    public Guid OperationId { get; }
    public IReadOnlyList<string> ResourceSet { get; }
    public ResearchDurabilityPhase DurabilityPhase { get; }
    public ulong AuthorityGeneration { get; }
    public IReadOnlyList<long> DependencyActionIds { get; }

    public static PersistenceAction FromResearchEvent(ResearchEvent researchEvent)
    {
        ArgumentNullException.ThrowIfNull(researchEvent);
        return new PersistenceAction(
            researchEvent.LogicalEventId,
            researchEvent.EventKind,
            researchEvent.HistoryId,
            researchEvent.ParentHistoryId,
            researchEvent.OperationId,
            researchEvent.ResourceSet,
            researchEvent.DurabilityPhase,
            researchEvent.AuthorityGeneration,
            researchEvent.DependencyEventIds);
    }
}

/// <summary>
/// Sound-mode independence is intentionally conservative. Different HistoryId
/// values alone never imply independence. Shared resources, ancestry, explicit
/// dependencies, one logical operation, and authority/lifecycle transitions all
/// force dependence.
/// </summary>
public interface IPersistenceActionIndependence
{
    bool AreIndependent(PersistenceAction left, PersistenceAction right);
}

public sealed class ConservativeHistoryIndependence : IPersistenceActionIndependence
{
    private readonly Dictionary<HistoryId, HistoryId?> _parents;

    public ConservativeHistoryIndependence(IEnumerable<PersistenceAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var parentMap = new Dictionary<HistoryId, HistoryId?>();
        foreach (var action in actions)
        {
            if (!parentMap.TryGetValue(action.HistoryId, out var existing))
            {
                parentMap.Add(action.HistoryId, action.ParentHistoryId);
            }
            else if (existing != action.ParentHistoryId)
            {
                throw new ArgumentException("A history cannot have conflicting parents in one bounded model.", nameof(actions));
            }
        }

        _parents = parentMap;
    }

    public bool AreIndependent(PersistenceAction left, PersistenceAction right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.ActionId == right.ActionId
            || left.HistoryId == right.HistoryId
            || left.OperationId == right.OperationId
            || left.DependencyActionIds.Contains(right.ActionId)
            || right.DependencyActionIds.Contains(left.ActionId)
            || left.ResourceSet.Intersect(right.ResourceSet, StringComparer.Ordinal).Any()
            || HasAncestrySensitiveCoupling(left, right)
            || IsGlobalTransition(left.EventKind)
            || IsGlobalTransition(right.EventKind))
        {
            return false;
        }

        return true;
    }

    private bool HasAncestrySensitiveCoupling(PersistenceAction left, PersistenceAction right)
        => (IsAncestor(left.HistoryId, right.HistoryId) || IsAncestor(right.HistoryId, left.HistoryId))
            && (IsAncestrySensitiveTransition(left.EventKind)
                || IsAncestrySensitiveTransition(right.EventKind));

    private static bool IsAncestrySensitiveTransition(ResearchEventKind kind)
        => kind is ResearchEventKind.RootTransition
            or ResearchEventKind.AuthorityAccepted
            or ResearchEventKind.HistoryValidated
            or ResearchEventKind.HistoryReady;

    private bool IsAncestor(HistoryId possibleAncestor, HistoryId history)
    {
        var visited = new HashSet<HistoryId>();
        var current = history;
        while (_parents.TryGetValue(current, out var parent) && parent is { } actualParent)
        {
            if (!visited.Add(current))
            {
                throw new InvalidOperationException("History parent map contains a cycle.");
            }

            if (actualParent == possibleAncestor)
            {
                return true;
            }

            current = actualParent;
        }

        return false;
    }

    private static bool IsGlobalTransition(ResearchEventKind kind)
        // Per-history authority publication is intentionally not global: separate
        // history WAL/checkpoint authority domains are one of the hypotheses P2
        // needs to test. Shared catalog/root resources and explicit dependencies
        // already force dependence where publication is actually coupled.
        => PersistenceActionIndependenceRules.IsGlobalTransition(kind);
}

/// <summary>
/// Generic resource/dependency POR baseline. It intentionally has no branch ancestry
/// model and therefore answers whether history topology adds anything beyond durable
/// resource touch sets and explicit dependency edges.
/// </summary>
public sealed class ResourceDependencyIndependence : IPersistenceActionIndependence
{
    public bool AreIndependent(PersistenceAction left, PersistenceAction right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.ActionId != right.ActionId
            && left.OperationId != right.OperationId
            && !left.DependencyActionIds.Contains(right.ActionId)
            && !right.DependencyActionIds.Contains(left.ActionId)
            && !left.ResourceSet.Intersect(right.ResourceSet, StringComparer.Ordinal).Any()
            && !PersistenceActionIndependenceRules.IsGlobalTransition(left.EventKind)
            && !PersistenceActionIndependenceRules.IsGlobalTransition(right.EventKind);
    }
}

internal static class PersistenceActionIndependenceRules
{
    public static bool IsGlobalTransition(ResearchEventKind kind)
        => kind is ResearchEventKind.RecoveryStarted
            or ResearchEventKind.RecoveryCompleted
            or ResearchEventKind.CorruptionDetected;
}

public sealed record RandomCrashSamplingResult(
    int SampleBudget,
    int UniqueCrashPlansSampled,
    int UniqueObservationTraceCount)
{
    public double ObservationCoverage(int exhaustiveObservationTraceCount)
        => exhaustiveObservationTraceCount <= 0
            ? 0d
            : (double)UniqueObservationTraceCount / exhaustiveObservationTraceCount;
}

public sealed record PorVerificationResult(
    int ExhaustiveOrderCount,
    int ReducedOrderCount,
    int ExhaustiveCrashPlanCount,
    int ReducedCrashPlanCount,
    int ExhaustiveObservationTraceCount,
    int ReducedObservationTraceCount,
    bool ObservationSetsEquivalent)
{
    public double OrderReductionFactor => ReducedOrderCount == 0
        ? 0d
        : (double)ExhaustiveOrderCount / ReducedOrderCount;

    public double CrashPlanReductionFactor => ReducedCrashPlanCount == 0
        ? 0d
        : (double)ExhaustiveCrashPlanCount / ReducedCrashPlanCount;
}

/// <summary>
/// Small-state exhaustive oracle and POR representative generator for P2. It is
/// intentionally bounded: the paper's soundness evidence comes from exact equality
/// against this oracle before using the reducer at larger scales.
/// </summary>
public sealed class BoundedCrashPorExplorer
{
    private readonly PersistenceAction[] _actions;
    private readonly Dictionary<long, PersistenceAction> _byId;
    private readonly Dictionary<long, HashSet<long>> _requiredPredecessors;
    private readonly IPersistenceActionIndependence _independence;

    public BoundedCrashPorExplorer(
        IEnumerable<PersistenceAction> actions,
        int maximumActions = 9,
        IPersistenceActionIndependence? independence = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActions);

        _actions = actions.OrderBy(action => action.ActionId).ToArray();
        if (_actions.Length == 0)
        {
            throw new ArgumentException("A bounded POR model requires at least one action.", nameof(actions));
        }

        if (_actions.Length > maximumActions)
        {
            throw new ArgumentException(
                $"Bounded exhaustive exploration is limited to {maximumActions} actions; got {_actions.Length}.",
                nameof(actions));
        }

        if (_actions.GroupBy(action => action.ActionId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Action IDs must be unique.", nameof(actions));
        }

        _byId = _actions.ToDictionary(action => action.ActionId);
        if (_actions.SelectMany(action => action.DependencyActionIds).Any(id => !_byId.ContainsKey(id)))
        {
            throw new ArgumentException("Every explicit dependency must reference an action in the bounded model.", nameof(actions));
        }

        _requiredPredecessors = BuildRequiredPredecessors(_actions);
        _independence = independence ?? new ConservativeHistoryIndependence(_actions);
        EnsureAcyclic();
    }

    public IReadOnlyList<IReadOnlyList<PersistenceAction>> EnumerateExhaustiveOrders()
    {
        var result = new List<IReadOnlyList<PersistenceAction>>();
        Enumerate([], new HashSet<long>(), result);
        return result;
    }

    public IReadOnlyList<IReadOnlyList<PersistenceAction>> EnumerateReducedOrders()
    {
        var exhaustive = EnumerateExhaustiveOrders();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IReadOnlyList<PersistenceAction>>();
        foreach (var order in exhaustive)
        {
            var signature = CanonicalOrderSignature(order);
            if (seen.Add(signature))
            {
                result.Add(order);
            }
        }

        return result;
    }

    public IReadOnlyList<IReadOnlyList<PersistenceAction>> EnumerateExhaustiveCrashPrefixes()
    {
        var prefixes = new List<IReadOnlyList<PersistenceAction>>();
        foreach (var order in EnumerateExhaustiveOrders())
        {
            for (var length = 0; length <= order.Count; length++)
            {
                prefixes.Add(order.Take(length).ToArray());
            }
        }

        return prefixes;
    }

    public IReadOnlyList<IReadOnlyList<PersistenceAction>> EnumerateReducedCrashPrefixes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IReadOnlyList<PersistenceAction>>();
        foreach (var prefix in EnumerateExhaustiveCrashPrefixes())
        {
            if (seen.Add(CanonicalOrderSignature(prefix)))
            {
                result.Add(prefix);
            }
        }

        return result;
    }

    public PorVerificationResult VerifyCrashPrefixEquivalence(
        Func<IReadOnlyList<PersistenceAction>, CanonicalObservationTrace> evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        // Verification is intentionally streamed. The first P2 prototype materialized
        // every exhaustive order and every crash prefix several times, which made a
        // three-history real trace needlessly expensive. Streaming keeps the exact same
        // oracle semantics while retaining only canonical signatures and observation sets.
        var exhaustiveOrderCount = 0;
        var exhaustiveCrashPlanCount = 0;
        var reducedOrderSignatures = new HashSet<string>(StringComparer.Ordinal);
        var reducedCrashPlanSignatures = new HashSet<string>(StringComparer.Ordinal);
        var exhaustiveTraces = new HashSet<CanonicalTraceKey>();
        var reducedTraces = new HashSet<CanonicalTraceKey>();

        EnumerateOrders(order =>
        {
            exhaustiveOrderCount = checked(exhaustiveOrderCount + 1);
            reducedOrderSignatures.Add(CanonicalOrderSignature(order));

            for (var length = 0; length <= order.Count; length++)
            {
                exhaustiveCrashPlanCount = checked(exhaustiveCrashPlanCount + 1);
                var prefix = order.Take(length).ToArray();
                var traceKey = new CanonicalTraceKey(evaluator(prefix).Points);
                exhaustiveTraces.Add(traceKey);

                if (reducedCrashPlanSignatures.Add(CanonicalOrderSignature(prefix)))
                {
                    reducedTraces.Add(traceKey);
                }
            }
        });

        return new PorVerificationResult(
            exhaustiveOrderCount,
            reducedOrderSignatures.Count,
            exhaustiveCrashPlanCount,
            reducedCrashPlanSignatures.Count,
            exhaustiveTraces.Count,
            reducedTraces.Count,
            exhaustiveTraces.SetEquals(reducedTraces));
    }

    public RandomCrashSamplingResult SampleRandomCrashPlans(
        Func<IReadOnlyList<PersistenceAction>, CanonicalObservationTrace> evaluator,
        int sampleBudget,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleBudget);

        var random = new Random(seed);
        var plans = new HashSet<string>(StringComparer.Ordinal);
        var traces = new HashSet<CanonicalTraceKey>();
        for (var sample = 0; sample < sampleBudget; sample++)
        {
            var order = GenerateRandomOrder(random);
            var length = random.Next(0, order.Count + 1);
            var prefix = order.Take(length).ToArray();
            plans.Add(string.Join(',', prefix.Select(action => action.ActionId)));
            traces.Add(new CanonicalTraceKey(evaluator(prefix).Points));
        }

        return new RandomCrashSamplingResult(sampleBudget, plans.Count, traces.Count);
    }

    private List<PersistenceAction> GenerateRandomOrder(Random random)
    {
        var selected = new HashSet<long>();
        var order = new List<PersistenceAction>(_actions.Length);
        while (order.Count < _actions.Length)
        {
            var ready = _actions
                .Where(candidate => !selected.Contains(candidate.ActionId)
                    && _requiredPredecessors[candidate.ActionId].IsSubsetOf(selected))
                .ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidOperationException("Persistence action graph has no ready action.");
            }

            var candidate = ready[random.Next(ready.Length)];
            selected.Add(candidate.ActionId);
            order.Add(candidate);
        }

        return order;
    }

    private string CanonicalOrderSignature(IReadOnlyList<PersistenceAction> source)
    {
        var order = source.ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = 0; index < order.Length - 1; index++)
            {
                if (order[index].ActionId > order[index + 1].ActionId
                    && _independence.AreIndependent(order[index], order[index + 1]))
                {
                    (order[index], order[index + 1]) = (order[index + 1], order[index]);
                    changed = true;
                }
            }
        }

        return string.Join(',', order.Select(action => action.ActionId));
    }

    private void Enumerate(
        List<PersistenceAction> prefix,
        HashSet<long> selected,
        List<IReadOnlyList<PersistenceAction>> result)
    {
        if (prefix.Count == _actions.Length)
        {
            result.Add(prefix.ToArray());
            return;
        }

        foreach (var candidate in _actions)
        {
            if (selected.Contains(candidate.ActionId)
                || !_requiredPredecessors[candidate.ActionId].IsSubsetOf(selected))
            {
                continue;
            }

            selected.Add(candidate.ActionId);
            prefix.Add(candidate);
            Enumerate(prefix, selected, result);
            prefix.RemoveAt(prefix.Count - 1);
            selected.Remove(candidate.ActionId);
        }
    }

    private static Dictionary<long, HashSet<long>> BuildRequiredPredecessors(PersistenceAction[] actions)
    {
        var predecessors = actions.ToDictionary(action => action.ActionId, _ => new HashSet<long>());
        foreach (var action in actions)
        {
            predecessors[action.ActionId].UnionWith(action.DependencyActionIds);
        }

        // Program order within one durable history domain is never permuted by the
        // bounded model. Cross-history order is explored unless an explicit dependency
        // constrains it.
        foreach (var history in actions.GroupBy(action => action.HistoryId))
        {
            PersistenceAction? previous = null;
            foreach (var action in history.OrderBy(action => action.ActionId))
            {
                if (previous is not null)
                {
                    predecessors[action.ActionId].Add(previous.ActionId);
                }

                previous = action;
            }
        }

        return predecessors;
    }

    private void EnumerateOrders(Action<IReadOnlyList<PersistenceAction>> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        EnumerateOrders([], new HashSet<long>(), visitor);
    }

    private void EnumerateOrders(
        List<PersistenceAction> prefix,
        HashSet<long> selected,
        Action<IReadOnlyList<PersistenceAction>> visitor)
    {
        if (prefix.Count == _actions.Length)
        {
            visitor(prefix.ToArray());
            return;
        }

        foreach (var candidate in _actions)
        {
            if (selected.Contains(candidate.ActionId)
                || !_requiredPredecessors[candidate.ActionId].IsSubsetOf(selected))
            {
                continue;
            }

            selected.Add(candidate.ActionId);
            prefix.Add(candidate);
            EnumerateOrders(prefix, selected, visitor);
            prefix.RemoveAt(prefix.Count - 1);
            selected.Remove(candidate.ActionId);
        }
    }

    private void EnsureAcyclic()
    {
        var remaining = _requiredPredecessors
            .ToDictionary(pair => pair.Key, pair => new HashSet<long>(pair.Value));
        var ready = new Queue<long>(remaining
            .Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key)
            .Order());
        var visited = 0;

        while (ready.Count > 0)
        {
            var completed = ready.Dequeue();
            visited++;
            foreach (var pair in remaining.OrderBy(pair => pair.Key))
            {
                if (!pair.Value.Remove(completed) || pair.Value.Count != 0)
                {
                    continue;
                }

                ready.Enqueue(pair.Key);
            }
        }

        if (visited != _actions.Length)
        {
            throw new ArgumentException("Bounded persistence action dependencies contain a cycle.");
        }
    }

    private sealed class CanonicalTraceKey : IEquatable<CanonicalTraceKey>
    {
        private readonly ObservationTracePoint[] _points;

        public CanonicalTraceKey(IReadOnlyList<ObservationTracePoint> points)
        {
            _points = points.ToArray();
        }

        public bool Equals(CanonicalTraceKey? other)
            => other is not null && _points.AsSpan().SequenceEqual(other._points);

        public override bool Equals(object? obj) => obj is CanonicalTraceKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var point in _points)
            {
                hash.Add(point);
            }

            return hash.ToHashCode();
        }
    }
}
