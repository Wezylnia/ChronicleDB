using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class RecoveryReadinessPlannerTests
{
    private static readonly Guid Main = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid A = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Requested = Guid.Parse("40000000-0000-0000-0000-000000000001");

    [Fact]
    public void SafeRequestedFirstPreservesGlobalValidationAndReducesWorkToRequestedReady()
    {
        var planner = new RecoveryReadinessPlanner(
        [
            Profile(Main, null, 0, replay: 100),
            Profile(A, Main, 1, replay: 1_000),
            Profile(B, Main, 2, replay: 2_000),
            Profile(Requested, Main, 3, replay: 10),
        ]);

        var baseline = planner.Plan(Requested, RecoverySchedulingStrategy.RecoverAll);
        var requestedFirst = planner.Plan(Requested, RecoverySchedulingStrategy.ValidateAllRequestedReplayFirst);

        Assert.True(requestedFirst.PreservesGlobalFailClosedSemantics);
        Assert.Equal(baseline.TotalWork, requestedFirst.TotalWork);
        Assert.True(requestedFirst.WorkToRequestedReady < baseline.WorkToRequestedReady);
        Assert.True(requestedFirst.RequestedReadinessSpeedupAgainst(baseline) > 1d);
        Assert.Equal([Main, Requested], requestedFirst.RequestedDependencyClosure);

        var firstReplay = requestedFirst.Steps.First(step => step.To == RecoveryReadinessState.LocalReplayComplete);
        var lastGlobalValidation = requestedFirst.Steps.Last(step => step.IsGlobalValidationPhase);
        Assert.True(firstReplay.Sequence > lastGlobalValidation.Sequence);
    }

    [Fact]
    public void ReadinessPathIsExplicitAndOrdered()
    {
        var planner = new RecoveryReadinessPlanner([Profile(Main, null, 0, replay: 10)]);

        var plan = planner.Plan(Main, RecoverySchedulingStrategy.RecoverAll);

        Assert.Equal(
            [
                RecoveryReadinessState.MetadataValidated,
                RecoveryReadinessState.DependencyClosureValidated,
                RecoveryReadinessState.AuthorityValidated,
                RecoveryReadinessState.LocalReplayComplete,
                RecoveryReadinessState.Ready,
            ],
            plan.Steps.Select(step => step.To));
    }

    [Fact]
    public void AggressiveClosureFirstIsNotMislabelledAsFailClosedEquivalent()
    {
        var planner = new RecoveryReadinessPlanner(
        [
            Profile(Main, null, 0, replay: 100),
            Profile(A, Main, 1, replay: 1_000),
            Profile(Requested, Main, 2, replay: 10),
        ]);

        var plan = planner.Plan(Requested, RecoverySchedulingStrategy.RequestedDependencyClosureFirst);

        Assert.False(plan.PreservesGlobalFailClosedSemantics);
        var requestedReady = plan.Steps.Single(step => step.HistoryId == Requested && step.To == RecoveryReadinessState.Ready);
        var unrelatedDependencyValidation = plan.Steps.Single(
            step => step.HistoryId == A && step.To == RecoveryReadinessState.DependencyClosureValidated);
        Assert.True(requestedReady.Sequence < unrelatedDependencyValidation.Sequence);
    }

    [Fact]
    public void RejectsMissingParentAndCycles()
    {
        Assert.Throws<ArgumentException>(() => new RecoveryReadinessPlanner(
        [
            Profile(Main, Guid.NewGuid(), 0, replay: 1),
        ]));

        Assert.Throws<ArgumentException>(() => new RecoveryReadinessPlanner(
        [
            Profile(Main, A, 0, replay: 1),
            Profile(A, Main, 1, replay: 1),
        ]));
    }

    private static RecoveryHistoryWorkProfile Profile(Guid id, Guid? parent, int order, long replay)
        => new(
            id,
            parent,
            order,
            MetadataValidationWork: 5,
            DependencyValidationWork: 3,
            AuthorityValidationWork: 2,
            CheckpointLoadWork: replay / 2,
            WalReplayWork: replay - (replay / 2));
}
