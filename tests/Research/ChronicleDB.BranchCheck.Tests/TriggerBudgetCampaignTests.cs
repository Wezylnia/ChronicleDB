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
    public void DoltPortableGuidedSchedulesPrioritizeSequenceRelevantClassWithoutPickingOneExactRecipe()
    {
        DoltTriggerRecipeEvidence[] evidence = CreatePortableDoltEvidence(DoltHistoryImportRecipe.Pull);
        IReadOnlyList<DoltHistoryImportRecipe[]> guided =
            DoltTriggerBudgetCampaign.GenerateRiskPrioritizedOrderings(evidence);

        Assert.Equal(6, guided.Count);
        Assert.Equal(6, guided.Select(static ordering => string.Join(',', ordering)).Distinct(StringComparer.Ordinal).Count());
        foreach (DoltHistoryImportRecipe[] ordering in guided)
        {
            Assert.All(ordering.Take(3), recipe => Assert.True(DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe)));
            Assert.Equal(DoltHistoryImportRecipe.NoOp, ordering[3]);
        }

        for (int position = 0; position < 3; position++)
        {
            Assert.Equal(2, guided.Count(ordering => ordering[position] == DoltHistoryImportRecipe.Pull));
        }
    }

    [Fact]
    public void DoltPortableSyntheticSingleSequenceRelevantViolationProducesFairExpectedBudgetCurve()
    {
        DoltTriggerBudgetReport report = DoltTriggerBudgetCampaign.EvaluateEvidence(
            "Dolt synthetic",
            CreatePortableDoltEvidence(DoltHistoryImportRecipe.Pull));

        Assert.Equal(1, report.ViolationRecipeCount);
        Assert.Equal(3, report.SequenceRelevantRecipeCount);
        Assert.True(report.AllViolationsInsideSequenceRelevantClass);
        Assert.True(report.GuidedHasStrictAdvantageAtAnyBudget);
        Assert.Equal(
            [0.25, 0.5, 0.75, 1.0],
            report.BudgetCurve.Select(static point => Math.Round(point.GenericDetectionRate, 6)).ToArray());
        Assert.Equal(
            [0.333333, 0.666667, 1.0, 1.0],
            report.BudgetCurve.Select(static point => Math.Round(point.GuidedDetectionRate, 6)).ToArray());
    }

    [Fact]
    public void DoltPortableCandidateSetExplicitlyExcludesHardResetPortabilityOutlier()
    {
        Assert.Equal(
            [
                DoltHistoryImportRecipe.NoOp,
                DoltHistoryImportRecipe.FetchOnly,
                DoltHistoryImportRecipe.Pull,
                DoltHistoryImportRecipe.FetchMerge,
            ],
            DoltTriggerBudgetCampaign.PortableRecipes);
        Assert.DoesNotContain(DoltHistoryImportRecipe.FetchHardReset, DoltTriggerBudgetCampaign.PortableRecipes);
    }

    private static DoltTriggerRecipeEvidence[] CreatePortableDoltEvidence(DoltHistoryImportRecipe violatingRecipe)
        => DoltTriggerBudgetCampaign.PortableRecipes
            .Select(recipe => new DoltTriggerRecipeEvidence(
                recipe,
                DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe),
                recipe == violatingRecipe ? RelationStatus.Fail : RelationStatus.Pass,
                recipe == violatingRecipe ? "synthetic continuation divergence" : "synthetic continuation preserved",
                OutcomeClass.Success,
                OutcomeClass.Success,
                recipe == violatingRecipe ? "1" : "2",
                "2",
                "row-count=1;max-pk=1;continuation=" + (recipe == violatingRecipe ? "1" : "2"),
                "row-count=1;max-pk=2;continuation=2",
                null,
                recipe == violatingRecipe ? BaselineStatus.Detected : BaselineStatus.Pass,
                BaselineStatus.Pass,
                recipe == violatingRecipe))
            .ToArray();
}
