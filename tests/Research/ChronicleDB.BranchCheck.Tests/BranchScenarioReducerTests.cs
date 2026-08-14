using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class BranchScenarioReducerTests
{
    [Fact]
    public void ReducerRemovesIrrelevantFramesAndPreservesSemanticFailureSignature()
    {
        BranchScenario baseScenario = Assert.Single(
            SyntheticCampaign.Create(),
            scenario => scenario.Name == "mutation-continuation");
        CanonicalState state = baseScenario.BranchAtCreation;
        var noisyScenario = baseScenario with
        {
            Frames =
            [
                new TraceFrame(
                    "ordinary-read",
                    Success(state),
                    Success(state),
                    OperationClass: TraceOperationClass.GenericRead),
                baseScenario.Frames[0],
                new TraceFrame(
                    "ordinary-restart",
                    Success(state),
                    Success(state),
                    OperationClass: TraceOperationClass.Restart),
            ],
        };
        BranchCheckFailureSignature expected = BranchCheckFailureSignature.Capture(noisyScenario);

        TraceReductionResult result = BranchScenarioReducer.Reduce(
            noisyScenario,
            candidate => expected.Matches(candidate));

        Assert.Equal(3, result.Report.OriginalFrameCount);
        Assert.Equal(1, result.Report.ReducedFrameCount);
        Assert.Equal("continuation", Assert.Single(result.Scenario.Frames).Operation);
        Assert.Equal(expected.RelationKeys, result.Signature.RelationKeys);
        Assert.True(result.Report.RemovalAttempts >= 2);
    }

    [Fact]
    public void FailureSignatureIncludesRelationFamilyAndRejectsPassingScenario()
    {
        BranchScenario passing = Assert.Single(
            SyntheticCampaign.Create(),
            scenario => scenario.Name == "clean-control");

        Assert.False(BranchCheckFailureSignature.Capture(passing).IsFailure);
        Assert.Throws<ArgumentException>(() => BranchScenarioReducer.Reduce(passing, static _ => true));
    }

    private static ObserverObservation Success(CanonicalState state)
        => new(OutcomeClass.Success, state);
}
