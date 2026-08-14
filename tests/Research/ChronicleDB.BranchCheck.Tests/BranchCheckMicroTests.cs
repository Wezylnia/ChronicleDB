using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class BranchCheckMicroTests
{
    [Fact]
    public void MutationScenariosFailTheirExpectedBranchRelation()
    {
        foreach (BranchScenario scenario in SyntheticCampaign.Create().Where(static scenario => scenario.ExpectedFailingRelationId is not null))
        {
            ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
            RelationResult expected = Assert.Single(
                report.Relations,
                result => string.Equals(result.RelationId, scenario.ExpectedFailingRelationId, StringComparison.Ordinal));

            Assert.Equal(RelationStatus.Fail, expected.Status);
        }
    }

    [Fact]
    public void CleanControlPassesAllApplicableRelations()
    {
        BranchScenario scenario = Assert.Single(SyntheticCampaign.Create(), static scenario => scenario.Name == "clean-control");
        ScenarioReport report = BranchCheckRunner.Evaluate(scenario);

        Assert.False(report.BranchCheckDetected);
        Assert.All(
            report.Relations,
            static result => Assert.NotEqual(RelationStatus.Fail, result.Status));
    }

    [Fact]
    public void TemporalBoundaryUsesCapabilityDeclaredComponentsInsteadOfUniversalComponentSet()
    {
        var declared = new BranchBoundary("main", 10);
        var state = CanonicalState.Create(
            [new KeyValuePair<string, string>("k", "v")],
            "schema",
            "metadata",
            componentBoundaries: new Dictionary<string, BranchBoundary>(StringComparer.Ordinal)
            {
                ["data"] = declared,
                ["metadata"] = declared,
                ["continuation"] = new BranchBoundary("child", 0),
            });
        var scenario = new BranchScenario(
            "capability-boundary",
            BranchCapabilityProfile.Create(
                "ChronicleDB-like",
                supportsHistoricalFork: true,
                sourceBoundaryComponents: ["data", "metadata"]),
            declared,
            state,
            state,
            []);

        RelationResult result = new TemporalBoundaryRelation().Evaluate(scenario);
        Assert.Equal(RelationStatus.Pass, result.Status);
    }

    [Fact]
    public void GenericStateBaselineDoesNotInspectSpecializedObserverPaths()
    {
        HistoricalIssueCase issue = Assert.Single(
            HistoricalIssueCampaign.Create(),
            static issue => issue.System == "SlateDB" && issue.IssueNumber == 1902);

        ScenarioReport report = BranchCheckRunner.Evaluate(issue.Scenario);
        BaselineResult generic = Assert.Single(
            report.Baselines,
            static result => result.BaselineId == "B2.generic-state-differential");
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(issue.Scenario);
        RelationResult observer = Assert.Single(
            report.Relations,
            static result => result.RelationId == "BC.observer-dependency");

        Assert.Equal(BaselineStatus.Pass, generic.Status);
        Assert.Equal(BaselineStatus.NotApplicable, branchGrammar.Status);
        Assert.Equal(RelationStatus.Fail, observer.Status);
    }

    [Fact]
    public void RecoveryBaselineActsAsNegativeControlForRestartFailures()
    {
        HistoricalIssueCase issue = Assert.Single(
            HistoricalIssueCampaign.Create(),
            static issue => issue.System == "YugabyteDB" && issue.IssueNumber == 32057);

        ScenarioReport report = BranchCheckRunner.Evaluate(issue.Scenario);
        BaselineResult recovery = Assert.Single(
            report.Baselines,
            static result => result.BaselineId == "B3.generic-recovery");
        RelationResult relation = Assert.Single(
            report.Relations,
            static result => result.RelationId == "BC.recovery");

        Assert.Equal(BaselineStatus.Detected, recovery.Status);
        Assert.Equal(RelationStatus.Fail, relation.Status);
    }

    [Fact]
    public void BranchGrammarBaselineRemovesEasyLifecycleAndDiffCasesFromStrictUniqueSet()
    {
        HistoricalIssueCase matrix = Assert.Single(
            HistoricalIssueCampaign.Create(),
            static issue => issue.System == "MatrixOne" && issue.IssueNumber == 26120);
        HistoricalIssueCase dolt = Assert.Single(
            HistoricalIssueCampaign.Create(),
            static issue => issue.System == "Dolt" && issue.IssueNumber == 7106);

        Assert.Equal(BaselineStatus.Detected, AdversarialBaselineSuite.EvaluateBranchGrammar(matrix.Scenario).Status);
        Assert.Equal(BaselineStatus.Detected, AdversarialBaselineSuite.EvaluateBranchGrammar(dolt.Scenario).Status);
    }

    [Fact]
    public void HistoricalCampaignStillContainsAtLeastOneStrictRelationSpecificCaseAfterB4Attack()
    {
        HistoricalIssueCase[] cases = HistoricalIssueCampaign.Create().ToArray();
        int strictUnique = 0;
        foreach (HistoricalIssueCase issue in cases)
        {
            ScenarioReport report = BranchCheckRunner.Evaluate(issue.Scenario);
            BaselineResult b4 = AdversarialBaselineSuite.EvaluateBranchGrammar(issue.Scenario);
            if (report.BranchCheckDetected && !AdversarialBaselineSuite.AnyGenericBaselineDetected(report, b4))
            {
                strictUnique++;
            }
        }

        Assert.Equal(7, cases.Length);
        Assert.True(cases.Select(static issue => issue.System).Distinct(StringComparer.Ordinal).Count() >= 5);
        Assert.True(strictUnique >= 1);
    }

    [Fact]
    public void IncompleteHistoricalCreationEvidenceIsNotSilentlyPromotedToBaselinePass()
    {
        HistoricalIssueCase issue = Assert.Single(
            HistoricalIssueCampaign.Create(),
            static issue => issue.System == "MatrixOne" && issue.IssueNumber == 27092);

        BaselineResult b1 = Assert.Single(
            BranchCheckRunner.Evaluate(issue.Scenario).Baselines,
            static result => result.BaselineId == "B1.creation-visible-state");

        Assert.Equal(BaselineStatus.Inconclusive, b1.Status);
    }
}
