namespace ChronicleDB.Diagnostics.Research;

public enum ObserverScopedErasureOperation : byte
{
    AuthorizeForce = 0,
    PublishAuthority = 1,
    RewriteCheckpoint = 2,
    RewriteWal = 3,
    RewritePhysicalData = 4,
    MarkPhysicalScanIncomplete = 5,
    RestorePhysicalScanCompleteness = 6,
    AcknowledgeErasure = 7,
    Crash = 8,
    Recover = 9,
}

public enum ObserverScopedErasureMutant : byte
{
    None = 0,
    RecoverIgnoresDurableAuthority = 1,
    PrematureAcknowledgement = 2,
    RewriteBeforeAuthority = 3,
    AuthorityRevokesUnrelatedObservation = 4,
    AuthorityRevokesNonBlockingTargetObservation = 5,
    PublishGenericRedactionScope = 6,
}

public sealed record ObserverScopedErasureState(
    bool ForceAuthorized,
    bool AuthorityDurable,
    bool RuntimeAuthorityLoaded,
    bool CheckpointContainsTarget,
    bool WalContainsTarget,
    bool PhysicalDataContainsTarget,
    bool PhysicalScanComplete,
    bool RewriteOccurred,
    bool Acknowledged,
    bool Crashed,
    bool Ready,
    bool NonTargetObservationIntact,
    bool NonBlockingTargetObservationIntact,
    bool AuthorityScopeMatchesExactPlan)
{
    public static ObserverScopedErasureState Initial { get; } = new(
        ForceAuthorized: false,
        AuthorityDurable: false,
        RuntimeAuthorityLoaded: false,
        CheckpointContainsTarget: true,
        WalContainsTarget: true,
        PhysicalDataContainsTarget: true,
        PhysicalScanComplete: true,
        RewriteOccurred: false,
        Acknowledged: false,
        Crashed: false,
        Ready: true,
        NonTargetObservationIntact: true,
        NonBlockingTargetObservationIntact: true,
        AuthorityScopeMatchesExactPlan: true);

    public bool AnyTargetRepresentationRemains =>
        CheckpointContainsTarget || WalContainsTarget || PhysicalDataContainsTarget;

    public bool RevokedTargetValueCanBeServed =>
        Ready && !RuntimeAuthorityLoaded && AnyTargetRepresentationRemains;
}

public sealed record ObserverScopedErasureViolation(
    string Invariant,
    IReadOnlyList<ObserverScopedErasureOperation> Trace,
    ObserverScopedErasureState State);

public sealed record ObserverScopedErasureExplorationResult(
    int MaxDepth,
    int UniqueStateCount,
    int TransitionCount,
    IReadOnlyList<ObserverScopedErasureViolation> Violations)
{
    public bool IsSafe => Violations.Count == 0;
}

/// <summary>
/// Bounded executable model for A8-O3. This is a falsification model, not a proof of
/// ChronicleDB. It models only the protocol distinction that matters to the surviving
/// claim: an observer-scoped semantic authority may become durable before stale bytes
/// are rewritten, but erasure acknowledgement requires both semantic authority and a
/// complete non-reconstructing representation closure. Recovery must honor the durable
/// authority before serving reads. Unrelated observations must remain unchanged.
/// </summary>
public static class ObserverScopedErasureAuthorityModel
{
    private static readonly ObserverScopedErasureOperation[] Operations =
        Enum.GetValues<ObserverScopedErasureOperation>();

    public static ObserverScopedErasureExplorationResult Explore(
        int maxDepth,
        ObserverScopedErasureMutant mutant = ObserverScopedErasureMutant.None)
    {
        if (maxDepth is < 0 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Bounded exploration depth must be between 0 and 12.");
        }

        var initial = ObserverScopedErasureState.Initial;
        var visited = new HashSet<ObserverScopedErasureState> { initial };
        var frontier = new Queue<(ObserverScopedErasureState State, ObserverScopedErasureOperation[] Trace)>();
        frontier.Enqueue((initial, []));
        var violations = new List<ObserverScopedErasureViolation>();
        var violationKeys = new HashSet<string>(StringComparer.Ordinal);
        var transitions = 0;

        Check(initial, [], violations, violationKeys);
        while (frontier.Count != 0)
        {
            var (state, trace) = frontier.Dequeue();
            if (trace.Length >= maxDepth)
            {
                continue;
            }

            foreach (var operation in Operations)
            {
                if (!TryApply(state, operation, mutant, out var next))
                {
                    continue;
                }

                transitions++;
                var nextTrace = new ObserverScopedErasureOperation[trace.Length + 1];
                trace.CopyTo(nextTrace, 0);
                nextTrace[^1] = operation;
                Check(next, nextTrace, violations, violationKeys);
                if (visited.Add(next))
                {
                    frontier.Enqueue((next, nextTrace));
                }
            }
        }

        return new ObserverScopedErasureExplorationResult(
            maxDepth,
            visited.Count,
            transitions,
            Array.AsReadOnly(violations.ToArray()));
    }

    private static bool TryApply(
        ObserverScopedErasureState state,
        ObserverScopedErasureOperation operation,
        ObserverScopedErasureMutant mutant,
        out ObserverScopedErasureState next)
    {
        next = state;
        switch (operation)
        {
            case ObserverScopedErasureOperation.AuthorizeForce:
                if (state.Crashed || state.ForceAuthorized)
                {
                    return false;
                }

                next = state with { ForceAuthorized = true };
                return true;

            case ObserverScopedErasureOperation.PublishAuthority:
                if (state.Crashed || !state.ForceAuthorized || state.AuthorityDurable)
                {
                    return false;
                }

                next = state with
                {
                    AuthorityDurable = true,
                    RuntimeAuthorityLoaded = true,
                    NonTargetObservationIntact = mutant != ObserverScopedErasureMutant.AuthorityRevokesUnrelatedObservation,
                    NonBlockingTargetObservationIntact = mutant != ObserverScopedErasureMutant.AuthorityRevokesNonBlockingTargetObservation,
                    AuthorityScopeMatchesExactPlan = mutant != ObserverScopedErasureMutant.PublishGenericRedactionScope,
                };
                return true;

            case ObserverScopedErasureOperation.RewriteCheckpoint:
                return TryRewrite(
                    state,
                    mutant,
                    state.CheckpointContainsTarget,
                    rewritten => state with
                    {
                        CheckpointContainsTarget = !rewritten,
                        RewriteOccurred = state.RewriteOccurred || rewritten,
                    },
                    out next);

            case ObserverScopedErasureOperation.RewriteWal:
                return TryRewrite(
                    state,
                    mutant,
                    state.WalContainsTarget,
                    rewritten => state with
                    {
                        WalContainsTarget = !rewritten,
                        RewriteOccurred = state.RewriteOccurred || rewritten,
                    },
                    out next);

            case ObserverScopedErasureOperation.RewritePhysicalData:
                return TryRewrite(
                    state,
                    mutant,
                    state.PhysicalDataContainsTarget,
                    rewritten => state with
                    {
                        PhysicalDataContainsTarget = !rewritten,
                        RewriteOccurred = state.RewriteOccurred || rewritten,
                    },
                    out next);

            case ObserverScopedErasureOperation.MarkPhysicalScanIncomplete:
                if (state.Crashed || state.Acknowledged || !state.PhysicalScanComplete)
                {
                    return false;
                }

                next = state with { PhysicalScanComplete = false };
                return true;

            case ObserverScopedErasureOperation.RestorePhysicalScanCompleteness:
                if (state.Crashed || state.PhysicalScanComplete)
                {
                    return false;
                }

                next = state with { PhysicalScanComplete = true };
                return true;

            case ObserverScopedErasureOperation.AcknowledgeErasure:
                if (state.Crashed || state.Acknowledged || !state.ForceAuthorized)
                {
                    return false;
                }

                var closureComplete = state.AuthorityDurable
                    && state.PhysicalScanComplete
                    && !state.AnyTargetRepresentationRemains;
                if (!closureComplete && mutant != ObserverScopedErasureMutant.PrematureAcknowledgement)
                {
                    return false;
                }

                next = state with { Acknowledged = true };
                return true;

            case ObserverScopedErasureOperation.Crash:
                if (state.Crashed)
                {
                    return false;
                }

                next = state with
                {
                    Crashed = true,
                    Ready = false,
                    RuntimeAuthorityLoaded = false,
                };
                return true;

            case ObserverScopedErasureOperation.Recover:
                if (!state.Crashed)
                {
                    return false;
                }

                next = state with
                {
                    Crashed = false,
                    Ready = true,
                    RuntimeAuthorityLoaded = state.AuthorityDurable
                        && mutant != ObserverScopedErasureMutant.RecoverIgnoresDurableAuthority,
                };
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static bool TryRewrite(
        ObserverScopedErasureState state,
        ObserverScopedErasureMutant mutant,
        bool targetPresent,
        Func<bool, ObserverScopedErasureState> apply,
        out ObserverScopedErasureState next)
    {
        next = state;
        if (state.Crashed || !targetPresent)
        {
            return false;
        }

        if (!state.AuthorityDurable && mutant != ObserverScopedErasureMutant.RewriteBeforeAuthority)
        {
            return false;
        }

        next = apply(true);
        return true;
    }

    private static void Check(
        ObserverScopedErasureState state,
        IReadOnlyList<ObserverScopedErasureOperation> trace,
        ICollection<ObserverScopedErasureViolation> violations,
        ISet<string> violationKeys)
    {
        CheckInvariant(
            "DurableAuthority => RevokedTargetNotServedWhenReady",
            !state.AuthorityDurable || !state.RevokedTargetValueCanBeServed,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "Rewrite => DurableObserverScopedAuthority",
            !state.RewriteOccurred || state.AuthorityDurable,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "Acknowledge => DurableAuthorityAndCompletePhysicalClosure",
            !state.Acknowledged
                || (state.AuthorityDurable
                    && state.PhysicalScanComplete
                    && !state.AnyTargetRepresentationRemains),
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "NonTargetObservationStable",
            state.NonTargetObservationIntact,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "NonBlockingTargetObservationStable",
            state.NonBlockingTargetObservationIntact,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "DurableAuthority => ExactObserverScope",
            !state.AuthorityDurable || state.AuthorityScopeMatchesExactPlan,
            state,
            trace,
            violations,
            violationKeys);
    }

    private static void CheckInvariant(
        string name,
        bool holds,
        ObserverScopedErasureState state,
        IReadOnlyList<ObserverScopedErasureOperation> trace,
        ICollection<ObserverScopedErasureViolation> violations,
        ISet<string> violationKeys)
    {
        if (holds)
        {
            return;
        }

        var key = $"{name}|{string.Join(',', trace)}";
        if (violationKeys.Add(key))
        {
            violations.Add(new ObserverScopedErasureViolation(name, trace.ToArray(), state));
        }
    }
}
