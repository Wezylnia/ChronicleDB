#if CHRONICLEDB_ENGINE_AVAILABLE
using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class ChronicleDbControlledMutationStudyTests
{
    [Fact]
    public void CreationEqualityDoesNotDischargeControlledLatentMutations()
    {
        BranchScenario baseline = ChronicleDbScenarioAdapter.ExecuteHistoricalForkRoundTrip();
        ControlledMutation[] mutations =
        [
            new("fractured-boundary", MutateBoundary, "BC.temporal-boundary"),
            new("stale-continuation", MutateContinuation, "BC.continuation-state"),
            new("missing-observer-dependency", MutateObserver, "BC.observer-dependency"),
            new("lost-recovery-lineage", MutateRecovery, "BC.recovery"),
            new("non-idempotent-lifecycle", MutateLifecycle, "BC.lifecycle"),
        ];

        foreach (ControlledMutation mutation in mutations)
        {
            BranchScenario mutated = mutation.Apply(baseline);
            ScenarioReport report = BranchCheckRunner.Evaluate(mutated);

            Assert.Equal(BaselineStatus.Pass, FindBaseline(report, "B0.creation-values").Status);
            Assert.Equal(BaselineStatus.Pass, FindBaseline(report, "B1.creation-visible-state").Status);
            RelationResult relation = FindRelation(report, mutation.ExpectedRelationId);
            Assert.Equal(RelationStatus.Fail, relation.Status);
        }
    }

    [Fact]
    public void ReducerPreservesAControlledMutationSignature()
    {
        BranchScenario baseline = ChronicleDbScenarioAdapter.ExecuteHistoricalForkRoundTrip();
        BranchScenario mutated = MutateContinuation(baseline);
        CanonicalState state = mutated.BranchAtCreation;
        var noisy = mutated with
        {
            Frames =
            [
                new TraceFrame("noise-read", Success(state), Success(state), OperationClass: TraceOperationClass.GenericRead),
                .. mutated.Frames,
                new TraceFrame("noise-restart", Success(state), Success(state), OperationClass: TraceOperationClass.Restart),
            ],
        };
        BranchCheckFailureSignature expected = BranchCheckFailureSignature.Capture(noisy);

        TraceReductionResult reduced = BranchScenarioReducer.Reduce(noisy, expected.Matches);

        Assert.True(expected.IsFailure);
        Assert.Equal(expected.RelationKeys, reduced.Signature.RelationKeys);
        Assert.True(reduced.Report.RemovedFrameCount >= 2);
    }

    private static BranchScenario MutateBoundary(BranchScenario scenario)
        => scenario with
        {
            BranchAtCreation = scenario.BranchAtCreation with
            {
                ComponentBoundaries = new Dictionary<string, BranchBoundary>(
                    scenario.BranchAtCreation.ComponentBoundaries ?? new Dictionary<string, BranchBoundary>(),
                    StringComparer.Ordinal)
                {
                    ["metadata"] = new BranchBoundary(
                        scenario.DeclaredBoundary.HistoryId,
                        checked(scenario.DeclaredBoundary.Sequence + 1)),
                },
            },
        };

    private static BranchScenario MutateContinuation(BranchScenario scenario)
        => ReplaceFrame(
            scenario,
            "continuation",
            frame => frame with
            {
                Branch = frame.Branch with
                {
                    State = frame.Branch.State! with { ContinuationToken = "mutated-token" },
                },
            });

    private static BranchScenario MutateObserver(BranchScenario scenario)
        => ReplaceFrame(
            scenario,
            "observe",
            frame => frame with
            {
                BranchObservers = new Dictionary<string, ObserverObservation>(
                    frame.BranchObservers!,
                    StringComparer.Ordinal)
                {
                    ["historical"] = new ObserverObservation(OutcomeClass.NotFound, null, "controlled dependency removal"),
                },
            });

    private static BranchScenario MutateRecovery(BranchScenario scenario)
        => ReplaceFrame(
            scenario,
            "restart",
            frame => frame with
            {
                Branch = new ObserverObservation(OutcomeClass.Crash, null, "controlled lost lineage"),
            });

    private static BranchScenario MutateLifecycle(BranchScenario scenario)
        => ReplaceFrame(
            scenario,
            "delete-branch",
            frame => frame with
            {
                Branch = new ObserverObservation(OutcomeClass.Rejected, null, "controlled non-idempotent retry"),
            });

    private static BranchScenario ReplaceFrame(
        BranchScenario scenario,
        string operation,
        Func<TraceFrame, TraceFrame> mutation)
        => scenario with
        {
            Frames = scenario.Frames
                .Select(frame => string.Equals(frame.Operation, operation, StringComparison.Ordinal)
                    ? mutation(frame)
                    : frame)
                .ToArray(),
        };

    private static BaselineResult FindBaseline(ScenarioReport report, string id)
        => Assert.Single(report.Baselines, result => string.Equals(result.BaselineId, id, StringComparison.Ordinal));

    private static RelationResult FindRelation(ScenarioReport report, string id)
        => Assert.Single(report.Relations, result => string.Equals(result.RelationId, id, StringComparison.Ordinal));

    private static ObserverObservation Success(CanonicalState state)
        => new(OutcomeClass.Success, state);

    private sealed record ControlledMutation(
        string Name,
        Func<BranchScenario, BranchScenario> Apply,
        string ExpectedRelationId);
}
#endif
