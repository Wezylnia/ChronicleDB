using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class DoltCloneContinuationSmokeTests
{
    [Fact]
    public void FreshCloneSuccessfulGeneratedInsertPassesContinuationAndCloneGrammar()
    {
        BranchScenario scenario = DoltCloneContinuationSmokeProbe.CreateScenario(
            "synthetic-good",
            OutcomeClass.Success,
            "1",
            string.Empty);

        RelationResult continuation = new ContinuationStateRelation().Evaluate(scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);

        Assert.Equal(RelationStatus.Pass, continuation.Status);
        Assert.Equal(BaselineStatus.Pass, branchGrammar.Status);
    }

    [Fact]
    public void FreshCloneRejectedContinuationFailsRelationWhileCloneGrammarStillPasses()
    {
        BranchScenario scenario = DoltCloneContinuationSmokeProbe.CreateScenario(
            "synthetic-context-cancel",
            OutcomeClass.Rejected,
            generatedId: null,
            "Error 1105 (HY000): context canceled");

        RelationResult continuation = new ContinuationStateRelation().Evaluate(scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);

        Assert.Equal(RelationStatus.Fail, continuation.Status);
        Assert.Contains("outcome diverged", continuation.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BaselineStatus.Pass, branchGrammar.Status);
    }

    [Fact]
    public void FreshCloneWrongGeneratedIdentifierFailsContinuationEvenWhenInsertSucceeds()
    {
        BranchScenario scenario = DoltCloneContinuationSmokeProbe.CreateScenario(
            "synthetic-wrong-id",
            OutcomeClass.Success,
            "2",
            string.Empty);

        RelationResult continuation = new ContinuationStateRelation().Evaluate(scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);

        Assert.Equal(RelationStatus.Fail, continuation.Status);
        Assert.Contains("branch=2", continuation.Evidence, StringComparison.Ordinal);
        Assert.Equal(BaselineStatus.Pass, branchGrammar.Status);
    }
}
