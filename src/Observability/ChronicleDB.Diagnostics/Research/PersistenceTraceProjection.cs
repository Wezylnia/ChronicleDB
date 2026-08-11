using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Projects complete persistence operations from a production research trace into
/// the bounded P2 action model. The projection is deliberately strict: an omitted
/// dependency is rejected instead of silently weakening the partial order.
/// </summary>
public static class PersistenceTraceSlice
{
    private static readonly ResearchEventKind[] RequiredKinds =
    [
        ResearchEventKind.OperationStarted,
        ResearchEventKind.DurabilityBarrier,
        ResearchEventKind.AuthorityPublished,
        ResearchEventKind.OperationCompleted,
    ];

    public static IReadOnlyList<PersistenceAction> SelectCompleteOperations(
        IEnumerable<ResearchEvent> events,
        int historyCount)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (historyCount is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCount));
        }

        var ordered = events.OrderBy(researchEvent => researchEvent.LogicalEventId).ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("A production trace slice cannot be empty.", nameof(events));
        }

        var candidates = ordered
            .GroupBy(researchEvent => researchEvent.OperationId)
            .Select(group => group.OrderBy(researchEvent => researchEvent.LogicalEventId).ToArray())
            .Where(IsCompleteOperation)
            .OrderBy(group => group[0].LogicalEventId)
            .ToArray();

        var selectedOperations = new List<ResearchEvent[]>(historyCount);
        var selectedHistories = new HashSet<HistoryId>();
        foreach (var candidate in candidates)
        {
            var historyId = candidate[0].HistoryId;
            if (!selectedHistories.Add(historyId))
            {
                continue;
            }

            selectedOperations.Add(candidate);
            if (selectedOperations.Count == historyCount)
            {
                break;
            }
        }

        if (selectedOperations.Count != historyCount)
        {
            throw new InvalidOperationException(
                $"Trace contains only {selectedOperations.Count} complete operations on distinct histories; {historyCount} required.");
        }

        var selectedEvents = selectedOperations
            .SelectMany(group => group)
            .OrderBy(researchEvent => researchEvent.LogicalEventId)
            .ToArray();
        var selectedIds = selectedEvents.Select(researchEvent => researchEvent.LogicalEventId).ToHashSet();
        var externalDependencies = selectedEvents
            .SelectMany(researchEvent => researchEvent.DependencyEventIds
                .Where(dependency => !selectedIds.Contains(dependency))
                .Select(dependency => (researchEvent.LogicalEventId, Dependency: dependency)))
            .ToArray();
        if (externalDependencies.Length != 0)
        {
            var first = externalDependencies[0];
            throw new InvalidOperationException(
                $"Trace slice would omit dependency {first.Dependency} required by event {first.LogicalEventId}.");
        }

        var remap = selectedEvents
            .Select((researchEvent, index) => (researchEvent.LogicalEventId, ActionId: (long)index + 1))
            .ToDictionary(pair => pair.LogicalEventId, pair => pair.ActionId);

        return Array.AsReadOnly(selectedEvents
            .Select(researchEvent => new PersistenceAction(
                remap[researchEvent.LogicalEventId],
                researchEvent.EventKind,
                researchEvent.HistoryId,
                researchEvent.ParentHistoryId,
                researchEvent.OperationId,
                researchEvent.ResourceSet,
                researchEvent.DurabilityPhase,
                researchEvent.AuthorityGeneration,
                researchEvent.DependencyEventIds.Select(dependency => remap[dependency])))
            .ToArray());
    }

    private static bool IsCompleteOperation(ResearchEvent[] events)
    {
        if (events.Length != RequiredKinds.Length
            || events.Select(researchEvent => researchEvent.HistoryId).Distinct().Count() != 1)
        {
            return false;
        }

        foreach (var kind in RequiredKinds)
        {
            if (events.Count(researchEvent => researchEvent.EventKind == kind) != 1)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Canonical observer for P2 slices derived from real ChronicleDB traces. Per-history
/// progress is normalized so truly independent histories can commute, while ordering
/// of actions that touch a shared durable resource remains observable. This oracle
/// does not reinterpret ResourceSet as a durability prerequisite; ResourceSet is the
/// durable-resource touch set used by the independence relation.
/// </summary>
public sealed class PersistenceTraceProjectionOracle
{
    private static readonly SafetyPredicateMask SafeMask = SafetyPredicateMask.NoPhantomCommit
        | SafetyPredicateMask.NoCrossHistoryReplay
        | SafetyPredicateMask.BaseStable
        | SafetyPredicateMask.NoInvalidRoot
        | SafetyPredicateMask.NoPrematureReclaim
        | SafetyPredicateMask.NoEarlyPublication;

    private readonly HashSet<string> _sharedResources;
    private readonly HistoryId _globalObserverHistoryId;

    public PersistenceTraceProjectionOracle(IEnumerable<PersistenceAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var materialized = actions.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A trace projection oracle requires at least one action.", nameof(actions));
        }

        _sharedResources = materialized
            .SelectMany(action => action.ResourceSet.Select(resource => (resource, action.HistoryId)))
            .GroupBy(item => item.resource, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.HistoryId).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var commonParents = materialized
            .Select(action => action.ParentHistoryId)
            .Where(parent => parent.HasValue)
            .Select(parent => parent!.Value)
            .Distinct()
            .ToArray();
        _globalObserverHistoryId = commonParents.Length == 1
            ? commonParents[0]
            : materialized.Select(action => action.HistoryId).OrderBy(history => history.Value).First();
    }

    public IReadOnlyCollection<string> SharedResources => _sharedResources;

    public CanonicalObservationTrace Evaluate(IReadOnlyList<PersistenceAction> prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var points = new List<ObservationTracePoint>();

        foreach (var history in prefix.GroupBy(action => action.HistoryId).OrderBy(group => group.Key.Value))
        {
            foreach (var action in history.OrderBy(action => action.ActionId))
            {
                points.Add(ToPoint(action, action.HistoryId, ComputeActionDigest(action)));
            }
        }

        // A shared durable resource is intentionally represented as one global
        // observation stream. Two orders that differ only in disjoint history-local
        // actions normalize together; two orders of branch-catalog publication do not.
        foreach (var action in prefix.Where(TouchesSharedResource))
        {
            var shared = action.ResourceSet.Where(_sharedResources.Contains).Order(StringComparer.Ordinal);
            var digest = ComputeDigest(
                "shared",
                action.HistoryId.Value.ToString("N"),
                ((byte)action.EventKind).ToString(CultureInfo.InvariantCulture),
                action.AuthorityGeneration.ToString(CultureInfo.InvariantCulture),
                string.Join(',', shared));
            points.Add(ToPoint(action, _globalObserverHistoryId, digest));
        }

        return new CanonicalObservationTrace(points);
    }

    private bool TouchesSharedResource(PersistenceAction action)
        => action.ResourceSet.Any(_sharedResources.Contains);

    private static ObservationTracePoint ToPoint(
        PersistenceAction action,
        HistoryId observerHistoryId,
        string digest)
        => new(
            action.EventKind,
            observerHistoryId,
            action.DurabilityPhase,
            action.EventKind == ResearchEventKind.OperationCompleted
                ? ObservationAvailability.Ready
                : action.EventKind == ResearchEventKind.AuthorityPublished
                    ? ObservationAvailability.AuthorityValidated
                    : ObservationAvailability.Unvalidated,
            ObservationErrorKind.None,
            corruptionDetected: false,
            action.AuthorityGeneration,
            SafeMask,
            digest,
            errorCode: null);

    private static string ComputeActionDigest(PersistenceAction action)
        => ComputeDigest(
            "history",
            action.HistoryId.Value.ToString("N"),
            ((byte)action.EventKind).ToString(CultureInfo.InvariantCulture),
            ((byte)action.DurabilityPhase).ToString(CultureInfo.InvariantCulture),
            action.AuthorityGeneration.ToString(CultureInfo.InvariantCulture),
            string.Join(',', action.ResourceSet));

    private static string ComputeDigest(params string[] values)
    {
        var text = string.Join('|', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
