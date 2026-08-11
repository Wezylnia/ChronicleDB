using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ErasureClosureAnalyzerTests
{
    private static readonly Guid Main = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid A = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid A1 = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("40000000-0000-0000-0000-000000000001");

    [Fact]
    public void GlobalClosureFindsObserverContractsAndEngineRepresentations()
    {
        var input = Input(scanComplete: true);

        var analysis = ErasureClosureAnalyzer.Analyze(input, ErasureScope.Global);

        Assert.Equal(4, analysis.ObserverHistoriesInScope.Count);
        Assert.Equal(2, analysis.BlockingObserverContracts.Count);
        Assert.Equal(2, analysis.MvccVersionOccurrences);
        Assert.Equal(1, analysis.WalOccurrences);
        Assert.Equal(1, analysis.CheckpointOccurrences);
        Assert.True(analysis.ClosureIsComplete);
    }

    [Fact]
    public void RequestBlocksRatherThanBreakingSnapshotOrBranchBaseContract()
    {
        var decision = ErasureContractEvaluator.Evaluate(Input(scanComplete: true), ErasureScope.Global, ErasureMode.Request);

        Assert.Equal(ErasureContractOutcome.BlockedByObserverContract, decision.Outcome);
        Assert.False(decision.CanAcknowledgeAfterPlanApplied);
        Assert.Equal(2, decision.RequiredRevocations.Count);
    }

    [Fact]
    public void ForceRequiresExplicitAuthorityAndPublishesRevocationsInPlan()
    {
        var unauthorized = ErasureContractEvaluator.Evaluate(Input(scanComplete: true), ErasureScope.Global, ErasureMode.Force);
        var authorized = ErasureContractEvaluator.Evaluate(Input(scanComplete: true), ErasureScope.Global, ErasureMode.Force, forceAuthorized: true);

        Assert.Equal(ErasureContractOutcome.ForceAuthorizationRequired, unauthorized.Outcome);
        Assert.Equal(ErasureContractOutcome.ForcePlanReady, authorized.Outcome);
        Assert.True(authorized.CanAcknowledgeAfterPlanApplied);
        Assert.Equal(2, authorized.RequiredRevocations.Count);
        Assert.NotEmpty(authorized.ProposedRewritePlan);
    }

    [Fact]
    public void IncompletePhysicalClosureCannotBeAcknowledged()
    {
        var decision = ErasureContractEvaluator.Evaluate(Input(scanComplete: false), ErasureScope.Global, ErasureMode.Force, forceAuthorized: true);

        Assert.Equal(ErasureContractOutcome.BlockedByIncompleteClosure, decision.Outcome);
        Assert.False(decision.CanAcknowledgeAfterPlanApplied);
    }

    [Fact]
    public void LocalScopeDoesNotSilentlyRevokeDescendantObservers()
    {
        var analysis = ErasureClosureAnalyzer.Analyze(Input(scanComplete: true), ErasureScope.Local);

        Assert.Equal([Main], analysis.ObserverHistoriesInScope);
        Assert.Single(analysis.BlockingObserverContracts);
        Assert.DoesNotContain(analysis.BlockingObserverContracts, item => item.OwnerHistoryId == A);
    }

    private static ErasureClosureInput Input(bool scanComplete)
        => new(
            "key-1",
            Main,
            [
                new ErasureHistoryNode(Main, null),
                new ErasureHistoryNode(A, Main),
                new ErasureHistoryNode(A1, A),
                new ErasureHistoryNode(B, Main),
            ],
            [
                new ErasureRepresentation("v-main-1", ErasureRepresentationKind.MvccVersion, Main, Main, 1, ErasureContentState.Value, false),
                new ErasureRepresentation("v-a1-1", ErasureRepresentationKind.MvccVersion, A1, A1, 1, ErasureContentState.Value, false),
                new ErasureRepresentation("snapshot-main", ErasureRepresentationKind.PersistentSnapshotRoot, Main, Main, 1, ErasureContentState.Value, true),
                new ErasureRepresentation("branch-base-a", ErasureRepresentationKind.BranchBaseRoot, A, Main, 1, ErasureContentState.Value, true),
                new ErasureRepresentation("wal-b", ErasureRepresentationKind.WalMutation, B, B, 2, ErasureContentState.Value, false),
                new ErasureRepresentation("checkpoint-main", ErasureRepresentationKind.CheckpointVersion, Main, Main, 1, ErasureContentState.Value, false),
                new ErasureRepresentation("derived-main", ErasureRepresentationKind.DerivedCurrentState, Main, Main, 1, ErasureContentState.Tombstone, false),
            ],
            scanComplete,
            scanComplete ? [] : ["chronicle.data stale/unreachable page content"]);
}
