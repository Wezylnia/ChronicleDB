namespace ChronicleDB.Diagnostics.Research;

public enum RecoveryCompositionOperation : byte
{
    CommitMain = 0,
    CreateBranch = 1,
    CommitChild = 2,
    CheckpointMain = 3,
    CheckpointChild = 4,
    Crash = 5,
    RecoverMain = 6,
    RecoverChild = 7,
}

public enum RecoveryCompositionMutant : byte
{
    None = 0,
    ChildBaseAheadOfDurableParent = 1,
    RecoverMainBeyondDurablePrefix = 2,
    RecoverChildBeyondDurablePrefix = 3,
}

public sealed record RecoveryCompositionState(
    int MainDurableSequence,
    int MainCheckpointSequence,
    bool ChildExists,
    int ChildBaseSequence,
    int ChildDurableSequence,
    int ChildCheckpointSequence,
    bool Crashed,
    bool MainReady,
    int MainRecoveredSequence,
    bool ChildReady,
    int ChildRecoveredSequence)
{
    public static RecoveryCompositionState Initial { get; } = new(
        MainDurableSequence: 0,
        MainCheckpointSequence: 0,
        ChildExists: false,
        ChildBaseSequence: 0,
        ChildDurableSequence: 0,
        ChildCheckpointSequence: 0,
        Crashed: false,
        MainReady: true,
        MainRecoveredSequence: 0,
        ChildReady: false,
        ChildRecoveredSequence: 0);
}

public sealed record RecoveryCompositionViolation(
    string Invariant,
    IReadOnlyList<RecoveryCompositionOperation> Trace,
    RecoveryCompositionState State);

public sealed record RecoveryCompositionExplorationResult(
    int MaxDepth,
    int UniqueStateCount,
    int TransitionCount,
    IReadOnlyList<RecoveryCompositionViolation> Violations)
{
    public bool IsSafe => Violations.Count == 0;
}

/// <summary>
/// Small executable model used only as a proof-feasibility spike for Candidate 10.
/// It is not a mechanized proof of ChronicleDB. The model deliberately keeps the
/// trusted transition relation explicit so that mutant transitions can demonstrate
/// that the checked invariants are non-vacuous.
/// </summary>
public static class RecoveryCompositionModel
{
    private static readonly RecoveryCompositionOperation[] Operations =
        Enum.GetValues<RecoveryCompositionOperation>();

    public static RecoveryCompositionExplorationResult Explore(
        int maxDepth,
        RecoveryCompositionMutant mutant = RecoveryCompositionMutant.None)
    {
        if (maxDepth is < 0 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Bounded exploration depth must be between 0 and 12.");
        }

        var initial = RecoveryCompositionState.Initial;
        var visited = new HashSet<RecoveryCompositionState> { initial };
        var frontier = new Queue<(RecoveryCompositionState State, RecoveryCompositionOperation[] Trace)>();
        frontier.Enqueue((initial, []));
        var violations = new List<RecoveryCompositionViolation>();
        var violationKeys = new HashSet<string>(StringComparer.Ordinal);
        var transitions = 0;

        Check(initial, [], violations, violationKeys);
        while (frontier.Count > 0)
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
                var nextTrace = new RecoveryCompositionOperation[trace.Length + 1];
                trace.CopyTo(nextTrace, 0);
                nextTrace[^1] = operation;
                Check(next, nextTrace, violations, violationKeys);
                if (visited.Add(next))
                {
                    frontier.Enqueue((next, nextTrace));
                }
            }
        }

        return new RecoveryCompositionExplorationResult(
            maxDepth,
            visited.Count,
            transitions,
            Array.AsReadOnly(violations.ToArray()));
    }

    private static bool TryApply(
        RecoveryCompositionState state,
        RecoveryCompositionOperation operation,
        RecoveryCompositionMutant mutant,
        out RecoveryCompositionState next)
    {
        next = state;
        switch (operation)
        {
            case RecoveryCompositionOperation.CommitMain:
                if (state.Crashed)
                {
                    return false;
                }

                next = state with
                {
                    MainDurableSequence = checked(state.MainDurableSequence + 1),
                    MainReady = true,
                    MainRecoveredSequence = checked(state.MainDurableSequence + 1),
                };
                return true;

            case RecoveryCompositionOperation.CreateBranch:
                if (state.Crashed || state.ChildExists)
                {
                    return false;
                }

                next = state with
                {
                    ChildExists = true,
                    ChildBaseSequence = mutant == RecoveryCompositionMutant.ChildBaseAheadOfDurableParent
                        ? checked(state.MainDurableSequence + 1)
                        : state.MainDurableSequence,
                    ChildReady = true,
                    ChildRecoveredSequence = 0,
                };
                return true;

            case RecoveryCompositionOperation.CommitChild:
                if (state.Crashed || !state.ChildExists)
                {
                    return false;
                }

                next = state with
                {
                    ChildDurableSequence = checked(state.ChildDurableSequence + 1),
                    ChildReady = true,
                    ChildRecoveredSequence = checked(state.ChildDurableSequence + 1),
                };
                return true;

            case RecoveryCompositionOperation.CheckpointMain:
                if (state.Crashed)
                {
                    return false;
                }

                next = state with { MainCheckpointSequence = state.MainDurableSequence };
                return true;

            case RecoveryCompositionOperation.CheckpointChild:
                if (state.Crashed || !state.ChildExists)
                {
                    return false;
                }

                next = state with { ChildCheckpointSequence = state.ChildDurableSequence };
                return true;

            case RecoveryCompositionOperation.Crash:
                if (state.Crashed)
                {
                    return false;
                }

                next = state with
                {
                    Crashed = true,
                    MainReady = false,
                    ChildReady = false,
                    MainRecoveredSequence = 0,
                    ChildRecoveredSequence = 0,
                };
                return true;

            case RecoveryCompositionOperation.RecoverMain:
                if (!state.Crashed || state.MainReady)
                {
                    return false;
                }

                next = state with
                {
                    MainReady = true,
                    MainRecoveredSequence = mutant == RecoveryCompositionMutant.RecoverMainBeyondDurablePrefix
                        ? checked(state.MainDurableSequence + 1)
                        : state.MainDurableSequence,
                };
                return true;

            case RecoveryCompositionOperation.RecoverChild:
                if (!state.Crashed || !state.ChildExists || state.ChildReady || !state.MainReady)
                {
                    return false;
                }

                next = state with
                {
                    ChildReady = true,
                    ChildRecoveredSequence = mutant == RecoveryCompositionMutant.RecoverChildBeyondDurablePrefix
                        ? checked(state.ChildDurableSequence + 1)
                        : state.ChildDurableSequence,
                };
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static void Check(
        RecoveryCompositionState state,
        IReadOnlyList<RecoveryCompositionOperation> trace,
        ICollection<RecoveryCompositionViolation> violations,
        ISet<string> violationKeys)
    {
        CheckInvariant(
            "Recover(Main) => LegalCommittedPrefix(Main)",
            !state.MainReady || state.MainRecoveredSequence <= state.MainDurableSequence,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "Recover(Child) => LegalCommittedPrefix(Child)",
            !state.ChildReady || state.ChildRecoveredSequence <= state.ChildDurableSequence,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "ChildBase => DurableRecoverableParentBoundary",
            !state.ChildExists || state.ChildBaseSequence <= state.MainDurableSequence,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "Checkpoint(Main) <= DurablePrefix(Main)",
            state.MainCheckpointSequence <= state.MainDurableSequence,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "Checkpoint(Child) <= DurablePrefix(Child)",
            !state.ChildExists || state.ChildCheckpointSequence <= state.ChildDurableSequence,
            state,
            trace,
            violations,
            violationKeys);

        CheckInvariant(
            "ChildReadyAfterCrash => ParentRecoveryAuthorityReady",
            !state.Crashed || !state.ChildReady || state.MainReady,
            state,
            trace,
            violations,
            violationKeys);
    }

    private static void CheckInvariant(
        string name,
        bool holds,
        RecoveryCompositionState state,
        IReadOnlyList<RecoveryCompositionOperation> trace,
        ICollection<RecoveryCompositionViolation> violations,
        ISet<string> violationKeys)
    {
        if (holds)
        {
            return;
        }

        var key = $"{name}|{string.Join(',', trace)}";
        if (violationKeys.Add(key))
        {
            violations.Add(new RecoveryCompositionViolation(name, trace.ToArray(), state));
        }
    }
}
