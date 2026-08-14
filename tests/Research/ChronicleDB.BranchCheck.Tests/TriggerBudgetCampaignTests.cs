using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class TriggerBudgetCampaignTests
{
    [Fact]
    public void FiveRecipeSpaceHasAllOneHundredTwentyOrderings()
    {
        MatrixOneIdentityMutationRecipe[] recipes = Enum.GetValues<MatrixOneIdentityMutationRecipe>();
        IReadOnlyList<MatrixOneIdentityMutationRecipe[]> permutations =
            MatrixOneTriggerBudgetCampaign.GeneratePermutations(recipes);

        Assert.Equal(120, permutations.Count);
        Assert.Equal(
            120,
            permutations.Select(static ordering => string.Join(',', ordering)).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryRecipeAppearsAtEveryPositionEquallyOften()
    {
        MatrixOneIdentityMutationRecipe[] recipes = Enum.GetValues<MatrixOneIdentityMutationRecipe>();
        IReadOnlyList<MatrixOneIdentityMutationRecipe[]> permutations =
            MatrixOneTriggerBudgetCampaign.GeneratePermutations(recipes);

        foreach (MatrixOneIdentityMutationRecipe recipe in recipes)
        {
            for (int position = 0; position < recipes.Length; position++)
            {
                Assert.Equal(24, permutations.Count(ordering => ordering[position] == recipe));
            }
        }
    }

    [Fact]
    public void DoltGuidedSchedulesPrioritizeRiskClassWithoutPickingOneExactRecipe()
    {
        DoltTriggerRecipeEvidence[] evidence = CreateDoltEvidence(DoltHistoryImportRecipe.Pull);
        IReadOnlyList<DoltHistoryImportRecipe[]> guided =
            DoltTriggerBudgetCampaign.GenerateRiskPrioritizedOrderings(evidence);

        Assert.Equal(12, guided.Count);
        Assert.Equal(12, guided.Select(static ordering => string.Join(',', ordering)).Distinct(StringComparer.Ordinal).Count());
        foreach (DoltHistoryImportRecipe[] ordering in guided)
        {
            Assert.All(ordering.Take(3), recipe => Assert.True(DoltHistoryImportSemantics.PublishesImportedRowsToCurrentHistory(recipe)));
            Assert.All(ordering.Skip(3), recipe => Assert.False(DoltHistoryImportSemantics.PublishesImportedRowsToCurrentHistory(recipe)));
        }

        Assert.Equal(4, guided.Count(ordering => ordering[0] == DoltHistoryImportRecipe.Pull));
        Assert.Equal(4, guided.Count(ordering => ordering[1] == DoltHistoryImportRecipe.Pull));
        Assert.Equal(4, guided.Count(ordering => ordering[2] == DoltHistoryImportRecipe.Pull));
    }

    [Fact]
    public void DoltSyntheticSingleHighRiskViolationProducesFairExpectedBudgetCurve()
    {
        DoltTriggerBudgetReport report = DoltTriggerBudgetCampaign.EvaluateEvidence(
            "Dolt synthetic",
            CreateDoltEvidence(DoltHistoryImportRecipe.Pull));

        Assert.Equal(1, report.ViolationRecipeCount);
        Assert.Equal(3, report.HighRiskRecipeCount);
        Assert.True(report.AllViolationsInsideHighRiskClass);
        Assert.True(report.GuidedHasStrictAdvantageAtAnyBudget);
        Assert.Equal(
            [0.2, 0.4, 0.6, 0.8, 1.0],
            report.BudgetCurve.Select(static point => Math.Round(point.GenericDetectionRate, 6)).ToArray());
        Assert.Equal(
            [0.333333, 0.666667, 1.0, 1.0, 1.0],
            report.BudgetCurve.Select(static point => Math.Round(point.GuidedDetectionRate, 6)).ToArray());
    }

    private static DoltTriggerRecipeEvidence[] CreateDoltEvidence(DoltHistoryImportRecipe violatingRecipe)
        => Enum.GetValues<DoltHistoryImportRecipe>()
            .Select(recipe => new DoltTriggerRecipeEvidence(
                recipe,
                DoltHistoryImportSemantics.PublishesImportedRowsToCurrentHistory(recipe),
                recipe == violatingRecipe ? RelationStatus.Fail : RelationStatus.Pass,
                recipe == violatingRecipe ? BaselineStatus.Detected : BaselineStatus.Pass,
                BaselineStatus.Pass,
                recipe == violatingRecipe))
            .ToArray();
}
