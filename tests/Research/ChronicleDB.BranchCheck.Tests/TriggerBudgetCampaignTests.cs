using System.Text.Json;
using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class TriggerBudgetCampaignTests
{
    [Fact]
    public void MatrixOneV2CandidateSetIsLargerAndSemanticClassContainsMultipleRecipes()
    {
        MatrixOneIdentityMutationRecipe[] recipes = Enum.GetValues<MatrixOneIdentityMutationRecipe>();
        MatrixOneIdentityMutationRecipe[] relevant = recipes
            .Where(MatrixOneIdentityMutationSemantics.IsIdentityStateRelevant)
            .ToArray();

        Assert.Equal(10, recipes.Length);
        Assert.Equal(
            [
                MatrixOneIdentityMutationRecipe.RenameSourceRoundTrip,
                MatrixOneIdentityMutationRecipe.RecreateSourceSameName,
                MatrixOneIdentityMutationRecipe.RecreateSourceSameNameSchemaVariant,
            ],
            relevant);
        Assert.DoesNotContain(MatrixOneIdentityMutationRecipe.RecreateUnrelatedObject, relevant);
        Assert.Equal(
            "1FA61958C7E97E5EC5BBC8F32D03D99BAAD902F5C360465A7594B8F053B52040",
            MatrixOneIdentityMutationSemantics.Fingerprint());
    }

    [Fact]
    public void MatrixOneV2PreregistrationArtifactMatchesCompiledCandidateGrammar()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "artifacts", "external-frozen", "matrixone-v2-preregistration.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement top = document.RootElement;

        Assert.Equal("pending-external-execution", top.GetProperty("status").GetString());
        Assert.Equal(MatrixOneIdentityMutationSemantics.Fingerprint(), top.GetProperty("candidate_set_fingerprint").GetString());
        Assert.Equal(10, top.GetProperty("candidate_count").GetInt32());
        Assert.Equal(3, top.GetProperty("identity_relevant_count").GetInt32());

        JsonElement.ArrayEnumerator rows = top.GetProperty("candidates").EnumerateArray();
        var frozen = rows.Select(row => (
                Recipe: row.GetProperty("recipe").GetString() ?? throw new InvalidOperationException("Frozen MatrixOne recipe name is null."),
                Relevant: row.GetProperty("identity_state_relevant").GetBoolean()))
            .ToArray();
        var compiled = Enum.GetValues<MatrixOneIdentityMutationRecipe>()
            .Select(recipe => (
                Recipe: recipe.ToString(),
                Relevant: MatrixOneIdentityMutationSemantics.IsIdentityStateRelevant(recipe)))
            .ToArray();

        Assert.Equal(compiled, frozen);
    }

    [Fact]
    public void MatrixOneV2SingleRelevantViolationUsesClassGuidanceRatherThanExactRecipe()
    {
        TriggerBudgetReport report = MatrixOneTriggerBudgetCampaign.EvaluateEvidence(
            CreateMatrixOneEvidence(MatrixOneIdentityMutationRecipe.RecreateSourceSameName));

        Assert.Equal(10, report.CandidateCount);
        Assert.Equal(3, report.IdentityRelevantRecipeCount);
        Assert.Equal(1, report.ViolationRecipeCount);
        Assert.True(report.AllViolationsInsideIdentityRelevantClass);
        Assert.True(report.GuidedHasStrictAdvantageAtAnyBudget);
        Assert.Equal(0.1, report.BudgetCurve[0].GenericDetectionRate, precision: 10);
        Assert.Equal(1.0 / 3.0, report.BudgetCurve[0].RelationGuidedDetectionRate, precision: 10);
        Assert.Equal(0.3, report.BudgetCurve[2].GenericDetectionRate, precision: 10);
        Assert.Equal(1.0, report.BudgetCurve[2].RelationGuidedDetectionRate, precision: 10);
    }

    [Fact]
    public void MatrixOneV2GuidanceDoesNotMagicallyDetectControlClassViolation()
    {
        TriggerBudgetReport report = MatrixOneTriggerBudgetCampaign.EvaluateEvidence(
            CreateMatrixOneEvidence(MatrixOneIdentityMutationRecipe.UpdateSourceRow));

        Assert.False(report.AllViolationsInsideIdentityRelevantClass);
        Assert.Equal(0.1, report.BudgetCurve[0].GenericDetectionRate, precision: 10);
        Assert.Equal(0.0, report.BudgetCurve[0].RelationGuidedDetectionRate, precision: 10);
        Assert.Equal(0.0, report.BudgetCurve[2].RelationGuidedDetectionRate, precision: 10);
        Assert.True(report.BudgetCurve[3].RelationGuidedDetectionRate > 0.0);
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

    private static TriggerRecipeEvidence[] CreateMatrixOneEvidence(MatrixOneIdentityMutationRecipe violatingRecipe)
        => Enum.GetValues<MatrixOneIdentityMutationRecipe>()
            .Select(recipe => new TriggerRecipeEvidence(
                recipe,
                MatrixOneIdentityMutationSemantics.IsIdentityStateRelevant(recipe),
                recipe == violatingRecipe ? RelationStatus.Fail : RelationStatus.Pass,
                BaselineStatus.Pass,
                BaselineStatus.Pass,
                recipe == violatingRecipe))
            .ToArray();

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
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChronicleDB.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate ChronicleDB repository root.");
    }

}
