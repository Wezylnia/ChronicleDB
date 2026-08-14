namespace ChronicleDB.BranchCheck;

public sealed record TriggerRecipeEvidence(
    MatrixOneIdentityMutationRecipe Recipe,
    RelationStatus BoundaryRelation,
    BaselineStatus GenericStateBaseline,
    BaselineStatus BranchGrammarBaseline,
    bool TriggeredBoundaryViolation);

public sealed record TriggerBudgetPoint(
    int CandidateBudget,
    int ExhaustiveOrderings,
    int GenericOrderingsDetected,
    double GenericDetectionRate,
    double RelationGuidedDetectionRate);

public sealed record TriggerBudgetReport(
    MatrixOneIdentityMutationRecipe GuidedRecipe,
    IReadOnlyList<TriggerRecipeEvidence> Recipes,
    IReadOnlyList<TriggerBudgetPoint> BudgetCurve,
    bool ExactlyOneViolationRecipe,
    bool GuidedRecipeIsViolation);

public static class MatrixOneTriggerBudgetCampaign
{
    public static async Task<TriggerBudgetReport> ExecuteAsync(
        SqlCliOptions options,
        CancellationToken cancellationToken = default)
    {
        MatrixOneIdentityMutationRecipe[] recipes = Enum.GetValues<MatrixOneIdentityMutationRecipe>();
        var evidence = new List<TriggerRecipeEvidence>(recipes.Length);
        foreach (MatrixOneIdentityMutationRecipe recipe in recipes)
        {
            BranchScenario scenario = await MatrixOneHistoricalIdentityAdapter.ExecuteAsync(
                options,
                recipe,
                cancellationToken).ConfigureAwait(false);
            ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
            RelationResult boundary = report.Relations.Single(static result => result.RelationId == "BC.temporal-boundary");
            BaselineResult genericState = report.Baselines.Single(static result => result.BaselineId == "B2.generic-state-differential");
            BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);
            evidence.Add(new TriggerRecipeEvidence(
                recipe,
                boundary.Status,
                genericState.Status,
                branchGrammar.Status,
                boundary.Status == RelationStatus.Fail));
        }

        MatrixOneIdentityMutationRecipe guidedRecipe = MatrixOneIdentityMutationRecipe.RecreateSourceSameName;
        IReadOnlyList<MatrixOneIdentityMutationRecipe[]> orderings = GeneratePermutations(recipes);
        var curve = new List<TriggerBudgetPoint>(recipes.Length);
        bool guidedDetected = evidence.Single(item => item.Recipe == guidedRecipe).TriggeredBoundaryViolation;
        for (int budget = 1; budget <= recipes.Length; budget++)
        {
            int detected = orderings.Count(ordering =>
                ordering.Take(budget).Any(recipe => evidence.Single(item => item.Recipe == recipe).TriggeredBoundaryViolation));
            curve.Add(new TriggerBudgetPoint(
                budget,
                orderings.Count,
                detected,
                detected / (double)orderings.Count,
                guidedDetected ? 1.0 : 0.0));
        }

        int violationCount = evidence.Count(static item => item.TriggeredBoundaryViolation);
        return new TriggerBudgetReport(
            guidedRecipe,
            evidence,
            curve,
            ExactlyOneViolationRecipe: violationCount == 1,
            GuidedRecipeIsViolation: guidedDetected);
    }

    public static IReadOnlyList<MatrixOneIdentityMutationRecipe[]> GeneratePermutations(
        IReadOnlyList<MatrixOneIdentityMutationRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        var working = recipes.ToArray();
        var output = new List<MatrixOneIdentityMutationRecipe[]>();
        Permute(working, 0, output);
        return output;
    }

    private static void Permute(
        MatrixOneIdentityMutationRecipe[] working,
        int index,
        ICollection<MatrixOneIdentityMutationRecipe[]> output)
    {
        if (index == working.Length)
        {
            output.Add((MatrixOneIdentityMutationRecipe[])working.Clone());
            return;
        }

        for (int current = index; current < working.Length; current++)
        {
            (working[index], working[current]) = (working[current], working[index]);
            Permute(working, index + 1, output);
            (working[index], working[current]) = (working[current], working[index]);
        }
    }
}
