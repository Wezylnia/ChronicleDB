using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Diagnostics.Research;

public sealed record PersistenceProtocolEvaluation(
    CanonicalObservationTrace Trace,
    IReadOnlyList<string> Violations)
{
    public bool IsSafe => Violations.Count == 0;
}

/// <summary>
/// Independent bounded protocol oracle for P2. It intentionally reasons only
/// about the declared research action model and never calls production recovery.
/// Independent cross-history interleavings are normalized into per-history
/// projections, while protocol/safety violations remain observable.
/// </summary>
public static class PersistenceProtocolOracle
{
    public static PersistenceProtocolEvaluation Evaluate(IReadOnlyList<PersistenceAction> prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        var operations = new Dictionary<Guid, OperationProtocolState>();

        foreach (var action in prefix)
        {
            if (!operations.TryGetValue(action.OperationId, out var state))
            {
                state = new OperationProtocolState(action.HistoryId);
                operations.Add(action.OperationId, state);
            }
            else if (state.OwnerHistory != action.HistoryId)
            {
                violations.Add($"cross-history-operation:{action.OperationId:N}");
            }

            if (action.EventKind == ResearchEventKind.DurabilityBarrier
                || action.DurabilityPhase == ResearchDurabilityPhase.StableStorageBarrier)
            {
                state.HasDurabilityBarrier = true;
                state.MaximumDurableGeneration = Math.Max(state.MaximumDurableGeneration, action.AuthorityGeneration);
                state.DurableResources.UnionWith(action.ResourceSet);
            }

            if (action.EventKind is ResearchEventKind.AuthorityAccepted or ResearchEventKind.AuthorityPublished)
            {
                if (!state.HasDurabilityBarrier)
                {
                    violations.Add($"early-authority:{action.OperationId:N}");
                }

                if (!action.ResourceSet.All(state.DurableResources.Contains))
                {
                    violations.Add($"undurable-resource-publication:{action.OperationId:N}");
                }

                if (action.AuthorityGeneration < state.MaximumDurableGeneration)
                {
                    violations.Add($"stale-authority-generation:{action.OperationId:N}");
                }

                state.AuthorityPublished = true;
                state.MaximumPublishedGeneration = Math.Max(state.MaximumPublishedGeneration, action.AuthorityGeneration);
            }

            if (action.DurabilityPhase == ResearchDurabilityPhase.Cleanup && !state.AuthorityPublished)
            {
                violations.Add($"premature-cleanup:{action.OperationId:N}");
            }
        }

        var safety = SafetyPredicateMask.NoPhantomCommit
            | SafetyPredicateMask.BaseStable
            | SafetyPredicateMask.NoInvalidRoot;
        if (!violations.Any(value => value.StartsWith("cross-history-operation:", StringComparison.Ordinal)))
        {
            safety |= SafetyPredicateMask.NoCrossHistoryReplay;
        }

        if (!violations.Any(value => value.StartsWith("premature-cleanup:", StringComparison.Ordinal)))
        {
            safety |= SafetyPredicateMask.NoPrematureReclaim;
        }

        if (!violations.Any(value => value.StartsWith("early-authority:", StringComparison.Ordinal)
            || value.StartsWith("undurable-resource-publication:", StringComparison.Ordinal)
            || value.StartsWith("stale-authority-generation:", StringComparison.Ordinal)))
        {
            safety |= SafetyPredicateMask.NoEarlyPublication;
        }

        var points = prefix
            .GroupBy(action => action.HistoryId)
            .OrderBy(group => group.Key.Value)
            .SelectMany(group => group
                .OrderBy(action => action.ActionId)
                .Select(action => new ObservationTracePoint(
                    action.EventKind,
                    action.HistoryId,
                    action.DurabilityPhase,
                    action.EventKind == ResearchEventKind.HistoryReady
                        ? ObservationAvailability.Ready
                        : ObservationAvailability.Unvalidated,
                    violations.Count == 0 ? ObservationErrorKind.None : ObservationErrorKind.InvalidTransition,
                    corruptionDetected: false,
                    action.AuthorityGeneration,
                    safety,
                    ComputeActionDigest(action),
                    violations.Count == 0 ? null : "research-protocol-violation")))
            .ToArray();

        return new PersistenceProtocolEvaluation(
            new CanonicalObservationTrace(points),
            Array.AsReadOnly(violations.ToArray()));
    }

    private static string ComputeActionDigest(PersistenceAction action)
    {
        var text = string.Join(
            '|',
            ((byte)action.EventKind).ToString(CultureInfo.InvariantCulture),
            action.HistoryId.Value.ToString("N"),
            ((byte)action.DurabilityPhase).ToString(CultureInfo.InvariantCulture),
            action.AuthorityGeneration.ToString(CultureInfo.InvariantCulture),
            string.Join(',', action.ResourceSet));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class OperationProtocolState(HistoryId ownerHistory)
    {
        public HistoryId OwnerHistory { get; } = ownerHistory;

        public bool HasDurabilityBarrier { get; set; }

        public bool AuthorityPublished { get; set; }

        public ulong MaximumDurableGeneration { get; set; }

        public ulong MaximumPublishedGeneration { get; set; }

        public HashSet<string> DurableResources { get; } = new(StringComparer.Ordinal);
    }
}

public sealed record PersistenceMutantResult(
    string Name,
    bool Killed,
    IReadOnlyList<string> Violations);

/// <summary>
/// Declared P2 mutation corpus. These are small protocol mutants used to verify
/// that the bounded observer notices the classes of persistence mistakes the
/// research claim says it preserves. This is adequacy evidence, not a soundness proof.
/// </summary>
public static class PersistenceMutationCorpus
{
    public static IReadOnlyList<PersistenceMutantResult> Evaluate()
    {
        var main = new HistoryId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var child = new HistoryId(Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var operation = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var otherOperation = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

        var cases = new Dictionary<string, IReadOnlyList<PersistenceAction>>(StringComparer.Ordinal)
        {
            ["missing-fsync"] =
            [
                Action(1, ResearchEventKind.OperationStarted, main, null, operation, ["main.wal"], ResearchDurabilityPhase.Prepared, 1),
                Action(2, ResearchEventKind.AuthorityPublished, main, null, operation, ["main.wal"], ResearchDurabilityPhase.AuthorityPublished, 1, [1]),
            ],
            ["early-wal-reset"] =
            [
                Action(1, ResearchEventKind.OperationStarted, main, null, operation, ["main.wal"], ResearchDurabilityPhase.Prepared, 1),
                Action(2, ResearchEventKind.RootTransition, main, null, operation, ["main.wal"], ResearchDurabilityPhase.Cleanup, 1, [1]),
            ],
            ["wrong-history-wal-replay"] =
            [
                Action(1, ResearchEventKind.DurabilityBarrier, main, null, operation, ["main.wal"], ResearchDurabilityPhase.StableStorageBarrier, 1),
                Action(2, ResearchEventKind.AuthorityPublished, child, main, operation, ["main.wal"], ResearchDurabilityPhase.AuthorityPublished, 1, [1]),
            ],
            ["premature-checkpoint-publication"] =
            [
                Action(1, ResearchEventKind.OperationStarted, main, null, operation, ["main.checkpoint"], ResearchDurabilityPhase.Prepared, 2),
                Action(2, ResearchEventKind.AuthorityPublished, main, null, operation, ["main.checkpoint"], ResearchDurabilityPhase.AuthorityPublished, 2, [1]),
            ],
            ["early-root-release"] =
            [
                Action(1, ResearchEventKind.OperationStarted, child, main, operation, ["history-roots"], ResearchDurabilityPhase.Prepared, 3),
                Action(2, ResearchEventKind.RootTransition, child, main, operation, ["history-roots"], ResearchDurabilityPhase.Cleanup, 3, [1]),
            ],
            ["parent-before-child-deletion"] =
            [
                Action(1, ResearchEventKind.OperationStarted, child, main, otherOperation, ["child.wal"], ResearchDurabilityPhase.Prepared, 1),
                Action(2, ResearchEventKind.RootTransition, main, null, operation, ["history-roots"], ResearchDurabilityPhase.Cleanup, 1),
            ],
            ["stale-authority-generation"] =
            [
                Action(1, ResearchEventKind.DurabilityBarrier, main, null, operation, ["main.wal"], ResearchDurabilityPhase.StableStorageBarrier, 5),
                Action(2, ResearchEventKind.AuthorityPublished, main, null, operation, ["main.wal"], ResearchDurabilityPhase.AuthorityPublished, 4, [1]),
            ],
            ["catalog-data-durability-inversion"] =
            [
                Action(1, ResearchEventKind.DurabilityBarrier, main, null, operation, ["catalog"], ResearchDurabilityPhase.StableStorageBarrier, 1),
                Action(2, ResearchEventKind.AuthorityPublished, main, null, operation, ["catalog", "main.data"], ResearchDurabilityPhase.AuthorityPublished, 1, [1]),
            ],
            ["compaction-early-cleanup"] =
            [
                Action(1, ResearchEventKind.OperationStarted, main, null, operation, ["main.data"], ResearchDurabilityPhase.Prepared, 1),
                Action(2, ResearchEventKind.OperationCompleted, main, null, operation, ["main.data"], ResearchDurabilityPhase.Cleanup, 1, [1]),
            ],
        };

        return Array.AsReadOnly(cases.Select(pair =>
        {
            var evaluation = PersistenceProtocolOracle.Evaluate(pair.Value);
            return new PersistenceMutantResult(pair.Key, !evaluation.IsSafe, evaluation.Violations);
        }).ToArray());
    }

    private static PersistenceAction Action(
        long id,
        ResearchEventKind kind,
        HistoryId history,
        HistoryId? parent,
        Guid operation,
        IEnumerable<string> resources,
        ResearchDurabilityPhase phase,
        ulong generation,
        IEnumerable<long>? dependencies = null)
        => new(id, kind, history, parent, operation, resources, phase, generation, dependencies);
}
