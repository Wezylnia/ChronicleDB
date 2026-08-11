namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Frozen research-only readiness contract for Candidate 17. These states do not
/// alter ChronicleDB's public recovery/open semantics.
/// </summary>
public enum RecoveryReadinessState : byte
{
    Discovered = 0,
    MetadataValidated = 1,
    DependencyClosureValidated = 2,
    AuthorityValidated = 3,
    LocalReplayComplete = 4,
    Ready = 5,
    Corrupt = 6,
    Unavailable = 7,
}

public enum RecoverySchedulingStrategy : byte
{
    RecoverAll = 0,
    ValidateAllRequestedReplayFirst = 1,
    RequestedDependencyClosureFirst = 2,
}

/// <summary>
/// Abstract, non-authoritative work profile for one independently durable history.
/// Work units may represent bytes, measured time, or a calibrated cost model, but
/// all profiles supplied to one planner invocation must use the same unit.
/// </summary>
public sealed record RecoveryHistoryWorkProfile(
    Guid HistoryId,
    Guid? ParentHistoryId,
    int BaselineOrder,
    long MetadataValidationWork,
    long DependencyValidationWork,
    long AuthorityValidationWork,
    long CheckpointLoadWork,
    long WalReplayWork)
{
    public long LocalReplayWork => checked(CheckpointLoadWork + WalReplayWork);
}

public sealed record RecoveryPlanStep(
    int Sequence,
    Guid HistoryId,
    RecoveryReadinessState From,
    RecoveryReadinessState To,
    long WorkUnits,
    bool IsGlobalValidationPhase);

public sealed record RecoverySchedulePlan(
    RecoverySchedulingStrategy Strategy,
    Guid RequestedHistoryId,
    IReadOnlyList<Guid> RequestedDependencyClosure,
    IReadOnlyList<RecoveryPlanStep> Steps,
    long WorkToRequestedReady,
    long TotalWork,
    bool PreservesGlobalFailClosedSemantics)
{
    public double RequestedReadinessSpeedupAgainst(RecoverySchedulePlan baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.RequestedHistoryId != RequestedHistoryId)
        {
            throw new ArgumentException("Recovery plans must target the same requested history.", nameof(baseline));
        }

        if (WorkToRequestedReady <= 0)
        {
            return baseline.WorkToRequestedReady <= 0 ? 1d : double.PositiveInfinity;
        }

        return (double)baseline.WorkToRequestedReady / WorkToRequestedReady;
    }
}

/// <summary>
/// Research-only scheduler used to test Candidate 17 before changing recovery
/// semantics. The safe requested-first strategy preserves a global validation
/// barrier and changes only expensive local replay ordering.
/// </summary>
public sealed class RecoveryReadinessPlanner
{
    private readonly RecoveryHistoryWorkProfile[] _profiles;
    private readonly Dictionary<Guid, RecoveryHistoryWorkProfile> _byHistory;

    public RecoveryReadinessPlanner(IEnumerable<RecoveryHistoryWorkProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var materialized = profiles.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("At least one history profile is required.", nameof(profiles));
        }

        ValidateProfiles(materialized);
        _profiles = materialized;
        _byHistory = materialized.ToDictionary(profile => profile.HistoryId);
    }

    public RecoverySchedulePlan Plan(Guid requestedHistoryId, RecoverySchedulingStrategy strategy)
    {
        if (!_byHistory.ContainsKey(requestedHistoryId))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedHistoryId), "Requested history does not exist in the workload profile.");
        }

        var closure = BuildDependencyClosure(requestedHistoryId);
        return strategy switch
        {
            RecoverySchedulingStrategy.RecoverAll => BuildSafePlan(requestedHistoryId, closure, requestedFirst: false),
            RecoverySchedulingStrategy.ValidateAllRequestedReplayFirst => BuildSafePlan(requestedHistoryId, closure, requestedFirst: true),
            RecoverySchedulingStrategy.RequestedDependencyClosureFirst => BuildAggressivePlan(requestedHistoryId, closure),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };
    }

    private RecoverySchedulePlan BuildSafePlan(
        Guid requestedHistoryId,
        IReadOnlyList<Guid> closure,
        bool requestedFirst)
    {
        var ordered = BaselineOrder().ToArray();
        var steps = new List<RecoveryPlanStep>(_profiles.Length * 5);
        var sequence = 0;

        // Safe contract: every history's metadata, ancestry/dependency relation,
        // and recovery authority are validated before any local replay can make a
        // history Ready. This preserves the v1.0 fail-closed validation boundary.
        foreach (var profile in ordered)
        {
            AddStep(profile, RecoveryReadinessState.Discovered, RecoveryReadinessState.MetadataValidated,
                profile.MetadataValidationWork, global: true);
        }

        foreach (var profile in ordered)
        {
            AddStep(profile, RecoveryReadinessState.MetadataValidated, RecoveryReadinessState.DependencyClosureValidated,
                profile.DependencyValidationWork, global: true);
        }

        foreach (var profile in ordered)
        {
            AddStep(profile, RecoveryReadinessState.DependencyClosureValidated, RecoveryReadinessState.AuthorityValidated,
                profile.AuthorityValidationWork, global: true);
        }

        IEnumerable<RecoveryHistoryWorkProfile> replayOrder = ordered;
        if (requestedFirst)
        {
            var closureSet = closure.ToHashSet();
            replayOrder = closure.Select(id => _byHistory[id])
                .Concat(ordered.Where(profile => !closureSet.Contains(profile.HistoryId)));
        }

        foreach (var profile in replayOrder)
        {
            AddStep(profile, RecoveryReadinessState.AuthorityValidated, RecoveryReadinessState.LocalReplayComplete,
                profile.LocalReplayWork, global: false);
            AddStep(profile, RecoveryReadinessState.LocalReplayComplete, RecoveryReadinessState.Ready,
                0, global: false);
        }

        return Finish(
            requestedFirst
                ? RecoverySchedulingStrategy.ValidateAllRequestedReplayFirst
                : RecoverySchedulingStrategy.RecoverAll,
            requestedHistoryId,
            closure,
            steps,
            preservesGlobalFailClosedSemantics: true);

        void AddStep(
            RecoveryHistoryWorkProfile profile,
            RecoveryReadinessState from,
            RecoveryReadinessState to,
            long work,
            bool global)
            => steps.Add(new RecoveryPlanStep(++sequence, profile.HistoryId, from, to, work, global));
    }

    private RecoverySchedulePlan BuildAggressivePlan(Guid requestedHistoryId, IReadOnlyList<Guid> closure)
    {
        var ordered = BaselineOrder().ToArray();
        var closureSet = closure.ToHashSet();
        var steps = new List<RecoveryPlanStep>(_profiles.Length * 5);
        var sequence = 0;

        // Metadata is still validated globally, but dependency/authority/replay for
        // the requested closure may run before unrelated histories are validated.
        // This is deliberately marked unsafe relative to the frozen v1.0 fail-closed
        // contract and must not be compared as if it provided identical semantics.
        foreach (var profile in ordered)
        {
            Add(profile, RecoveryReadinessState.Discovered, RecoveryReadinessState.MetadataValidated,
                profile.MetadataValidationWork, true);
        }

        foreach (var id in closure)
        {
            var profile = _byHistory[id];
            Add(profile, RecoveryReadinessState.MetadataValidated, RecoveryReadinessState.DependencyClosureValidated,
                profile.DependencyValidationWork, false);
            Add(profile, RecoveryReadinessState.DependencyClosureValidated, RecoveryReadinessState.AuthorityValidated,
                profile.AuthorityValidationWork, false);
            Add(profile, RecoveryReadinessState.AuthorityValidated, RecoveryReadinessState.LocalReplayComplete,
                profile.LocalReplayWork, false);
            Add(profile, RecoveryReadinessState.LocalReplayComplete, RecoveryReadinessState.Ready, 0, false);
        }

        foreach (var profile in ordered.Where(profile => !closureSet.Contains(profile.HistoryId)))
        {
            Add(profile, RecoveryReadinessState.MetadataValidated, RecoveryReadinessState.DependencyClosureValidated,
                profile.DependencyValidationWork, true);
            Add(profile, RecoveryReadinessState.DependencyClosureValidated, RecoveryReadinessState.AuthorityValidated,
                profile.AuthorityValidationWork, true);
            Add(profile, RecoveryReadinessState.AuthorityValidated, RecoveryReadinessState.LocalReplayComplete,
                profile.LocalReplayWork, false);
            Add(profile, RecoveryReadinessState.LocalReplayComplete, RecoveryReadinessState.Ready, 0, false);
        }

        return Finish(
            RecoverySchedulingStrategy.RequestedDependencyClosureFirst,
            requestedHistoryId,
            closure,
            steps,
            preservesGlobalFailClosedSemantics: false);

        void Add(
            RecoveryHistoryWorkProfile profile,
            RecoveryReadinessState from,
            RecoveryReadinessState to,
            long work,
            bool global)
            => steps.Add(new RecoveryPlanStep(++sequence, profile.HistoryId, from, to, work, global));
    }

    private static RecoverySchedulePlan Finish(
        RecoverySchedulingStrategy strategy,
        Guid requestedHistoryId,
        IReadOnlyList<Guid> closure,
        IReadOnlyList<RecoveryPlanStep> steps,
        bool preservesGlobalFailClosedSemantics)
    {
        long cumulative = 0;
        long? requestedReady = null;
        foreach (var step in steps)
        {
            cumulative = checked(cumulative + step.WorkUnits);
            if (step.HistoryId == requestedHistoryId && step.To == RecoveryReadinessState.Ready)
            {
                requestedReady ??= cumulative;
            }
        }

        if (requestedReady is null)
        {
            throw new InvalidOperationException("Recovery plan never makes the requested history Ready.");
        }

        return new RecoverySchedulePlan(
            strategy,
            requestedHistoryId,
            closure,
            Array.AsReadOnly(steps.ToArray()),
            requestedReady.Value,
            cumulative,
            preservesGlobalFailClosedSemantics);
    }

    private Guid[] BuildDependencyClosure(Guid requestedHistoryId)
    {
        var reversed = new List<Guid>();
        var seen = new HashSet<Guid>();
        var current = requestedHistoryId;
        while (true)
        {
            if (!seen.Add(current))
            {
                throw new InvalidOperationException("History ancestry contains a cycle.");
            }

            reversed.Add(current);
            var parent = _byHistory[current].ParentHistoryId;
            if (parent is null)
            {
                break;
            }

            current = parent.Value;
        }

        reversed.Reverse();
        return reversed.ToArray();
    }

    private IEnumerable<RecoveryHistoryWorkProfile> BaselineOrder()
        => _profiles.OrderBy(profile => profile.BaselineOrder).ThenBy(profile => profile.HistoryId);

    private static void ValidateProfiles(IReadOnlyList<RecoveryHistoryWorkProfile> profiles)
    {
        var ids = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (profile.HistoryId == Guid.Empty || !ids.Add(profile.HistoryId))
            {
                throw new ArgumentException("History profiles require unique non-empty IDs.", nameof(profiles));
            }

            if (profile.ParentHistoryId == profile.HistoryId)
            {
                throw new ArgumentException("A history cannot be its own parent.", nameof(profiles));
            }

            if (profile.BaselineOrder < 0
                || profile.MetadataValidationWork < 0
                || profile.DependencyValidationWork < 0
                || profile.AuthorityValidationWork < 0
                || profile.CheckpointLoadWork < 0
                || profile.WalReplayWork < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profiles), "Recovery work values must be non-negative.");
            }
        }

        foreach (var profile in profiles)
        {
            if (profile.ParentHistoryId is { } parent && !ids.Contains(parent))
            {
                throw new ArgumentException("Every parent history must exist in the same profile set.", nameof(profiles));
            }
        }

        // Validate acyclicity eagerly for every history, not only the requested one.
        var byId = profiles.ToDictionary(profile => profile.HistoryId);
        foreach (var profile in profiles)
        {
            var path = new HashSet<Guid>();
            var current = profile;
            while (true)
            {
                if (!path.Add(current.HistoryId))
                {
                    throw new ArgumentException("History ancestry contains a cycle.", nameof(profiles));
                }

                if (current.ParentHistoryId is not { } parent)
                {
                    break;
                }

                current = byId[parent];
            }
        }
    }
}
