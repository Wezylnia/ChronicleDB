using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class BranchCheckMicroTests
{
    private static readonly IBranchBaseline[] Baselines =
    [
        new CreationValuesBaseline(),
        new CreationVisibleStateBaseline(),
    ];

    private static readonly IBranchRelation[] Relations =
    [
        new ContinuationStateRelation(),
        new TemporalBoundaryRelation(),
        new LifecycleRelation(),
        new ObserverDependencyRelation(),
    ];

    [Fact]
    public void MutationScenariosPassCreationBaselinesButFailExpectedBranchRelation()
    {
        foreach (BranchScenario scenario in SyntheticCampaign.Create().Where(static scenario => scenario.ExpectedFailingRelationId is not null))
        {
            Assert.All(Baselines, baseline => Assert.True(baseline.Evaluate(scenario).Passed, $"{scenario.Name}: {baseline.Id} unexpectedly failed."));

            RelationResult expected = Assert.Single(
                Relations.Select(relation => relation.Evaluate(scenario)),
                result => string.Equals(result.RelationId, scenario.ExpectedFailingRelationId, StringComparison.Ordinal));

            Assert.Equal(RelationStatus.Fail, expected.Status);
        }
    }

    [Fact]
    public void CleanControlPassesAllApplicableRelations()
    {
        BranchScenario scenario = Assert.Single(SyntheticCampaign.Create(), static scenario => scenario.Name == "clean-control");

        Assert.All(Baselines, baseline => Assert.True(baseline.Evaluate(scenario).Passed));
        Assert.All(
            Relations,
            relation => Assert.NotEqual(RelationStatus.Fail, relation.Evaluate(scenario).Status));
    }

    [Fact]
    public void TemporalBoundaryIsNotApplicableWhenBackendDoesNotAdvertiseHistoricalFork()
    {
        BranchScenario source = Assert.Single(SyntheticCampaign.Create(), static scenario => scenario.Name == "mutation-temporal-boundary");
        BranchScenario scenario = source with
        {
            Capabilities = BranchCapabilityProfile.Create("synthetic", supportsHistoricalFork: false),
        };

        RelationResult result = new TemporalBoundaryRelation().Evaluate(scenario);
        Assert.Equal(RelationStatus.NotApplicable, result.Status);
    }

    [Fact]
    public void ObserverRelationIsNotApplicableWithoutDeclaredEquivalentObservers()
    {
        BranchScenario source = Assert.Single(SyntheticCampaign.Create(), static scenario => scenario.Name == "mutation-observer-dependency");
        BranchScenario scenario = source with
        {
            Capabilities = BranchCapabilityProfile.Create("synthetic"),
        };

        RelationResult result = new ObserverDependencyRelation().Evaluate(scenario);
        Assert.Equal(RelationStatus.NotApplicable, result.Status);
    }
}
