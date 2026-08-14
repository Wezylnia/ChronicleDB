namespace ChronicleDB.BranchCheck;

public sealed record DoltTriggerRecipeEvidence(
    DoltHistoryImportRecipe Recipe,
    bool HighRiskHistoryImport,
    RelationStatus ContinuationRelation,
    BaselineStatus GenericStateBaseline,
    BaselineStatus BranchGrammarBaseline,
    bool TriggeredViolation);

public sealed record DoltTriggerBudgetPoint(
    int CandidateBudget,
    int GenericOrderings,
    int GenericOrderingsDetected,
    double GenericDetectionRate,
    int GuidedOrderings,
    int GuidedOrderingsDetected,
    double GuidedDetectionRate);

public sealed record DoltTriggerBudgetReport(
    string BackendVersion,
    IReadOnlyList<DoltTriggerRecipeEvidence> Recipes,
    IReadOnlyList<DoltTriggerBudgetPoint> BudgetCurve,
    int ViolationRecipeCount,
    int HighRiskRecipeCount,
    bool AllViolationsInsideHighRiskClass,
    bool GuidedHasStrictAdvantageAtAnyBudget);

public static class DoltTriggerBudgetCampaign
{
    public static async Task<DoltTriggerBudgetReport> ExecuteAsync(
        DoltCliOptions options,
        CancellationToken cancellationToken = default)
    {
        DoltHistoryImportRecipe[] recipes = Enum.GetValues<DoltHistoryImportRecipe>();
        var evidence = new List<DoltTriggerRecipeEvidence>(recipes.Length);
        string backendVersion = "unknown";
        foreach (DoltHistoryImportRecipe recipe in recipes)
        {
            BranchScenario scenario = await DoltHistoryImportAdapter.ExecuteAsync(
                options,
                recipe,
                cancellationToken).ConfigureAwait(false);
            backendVersion = scenario.Capabilities.BackendName;
            ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
            RelationResult continuation = report.Relations.Single(static result => result.RelationId == "BC.continuation-state");
            BaselineResult genericState = report.Baselines.Single(static result => result.BaselineId == "B2.generic-state-differential");
            BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);
            evidence.Add(new DoltTriggerRecipeEvidence(
                recipe,
                DoltHistoryImportSemantics.PublishesImportedRowsToCurrentHistory(recipe),
                continuation.Status,
                genericState.Status,
                branchGrammar.Status,
                continuation.Status == RelationStatus.Fail));
        }

        return EvaluateEvidence(backendVersion, evidence);
    }

    public static DoltTriggerBudgetReport EvaluateEvidence(
        string backendVersion,
        IReadOnlyList<DoltTriggerRecipeEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendVersion);
        ArgumentNullException.ThrowIfNull(evidence);
        DoltHistoryImportRecipe[] recipes = evidence.Select(static item => item.Recipe).ToArray();
        if (recipes.Length == 0 || recipes.Distinct().Count() != recipes.Length)
        {
            throw new ArgumentException("Dolt budget evidence must contain unique recipes.", nameof(evidence));
        }

        IReadOnlyList<DoltHistoryImportRecipe[]> genericOrderings = GeneratePermutations(recipes);
        IReadOnlyList<DoltHistoryImportRecipe[]> guidedOrderings = GenerateRiskPrioritizedOrderings(evidence);
        var curve = new List<DoltTriggerBudgetPoint>(recipes.Length);
        for (int budget = 1; budget <= recipes.Length; budget++)
        {
            int genericDetected = CountDetected(genericOrderings, evidence, budget);
            int guidedDetected = CountDetected(guidedOrderings, evidence, budget);
            curve.Add(new DoltTriggerBudgetPoint(
                budget,
                genericOrderings.Count,
                genericDetected,
                genericDetected / (double)genericOrderings.Count,
                guidedOrderings.Count,
                guidedDetected,
                guidedDetected / (double)guidedOrderings.Count));
        }

        int violationCount = evidence.Count(static item => item.TriggeredViolation);
        int highRiskCount = evidence.Count(static item => item.HighRiskHistoryImport);
        bool allViolationsInsideHighRisk = evidence
            .Where(static item => item.TriggeredViolation)
            .All(static item => item.HighRiskHistoryImport);
        bool strictAdvantage = curve.Any(static point => point.GuidedDetectionRate > point.GenericDetectionRate);
        return new DoltTriggerBudgetReport(
            backendVersion,
            evidence.ToArray(),
            curve,
            violationCount,
            highRiskCount,
            allViolationsInsideHighRisk,
            strictAdvantage);
    }

    public static IReadOnlyList<DoltHistoryImportRecipe[]> GenerateRiskPrioritizedOrderings(
        IReadOnlyList<DoltTriggerRecipeEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        DoltHistoryImportRecipe[] highRisk = evidence
            .Where(static item => item.HighRiskHistoryImport)
            .Select(static item => item.Recipe)
            .ToArray();
        DoltHistoryImportRecipe[] lowRisk = evidence
            .Where(static item => !item.HighRiskHistoryImport)
            .Select(static item => item.Recipe)
            .ToArray();

        IReadOnlyList<DoltHistoryImportRecipe[]> highRiskOrderings = GeneratePermutations(highRisk);
        IReadOnlyList<DoltHistoryImportRecipe[]> lowRiskOrderings = GeneratePermutations(lowRisk);
        var combined = new List<DoltHistoryImportRecipe[]>(Math.Max(1, highRiskOrderings.Count) * Math.Max(1, lowRiskOrderings.Count));
        foreach (DoltHistoryImportRecipe[] high in highRiskOrderings.DefaultIfEmpty([]))
        {
            foreach (DoltHistoryImportRecipe[] low in lowRiskOrderings.DefaultIfEmpty([]))
            {
                combined.Add([.. high, .. low]);
            }
        }

        return combined;
    }

    public static IReadOnlyList<DoltHistoryImportRecipe[]> GeneratePermutations(
        IReadOnlyList<DoltHistoryImportRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        if (recipes.Count == 0)
        {
            return [[]];
        }

        var working = recipes.ToArray();
        var output = new List<DoltHistoryImportRecipe[]>();
        Permute(working, 0, output);
        return output;
    }

    private static int CountDetected(
        IReadOnlyList<DoltHistoryImportRecipe[]> orderings,
        IReadOnlyList<DoltTriggerRecipeEvidence> evidence,
        int budget)
        => orderings.Count(ordering =>
            ordering.Take(budget).Any(recipe => evidence.Single(item => item.Recipe == recipe).TriggeredViolation));

    private static void Permute(
        DoltHistoryImportRecipe[] working,
        int index,
        ICollection<DoltHistoryImportRecipe[]> output)
    {
        if (index == working.Length)
        {
            output.Add((DoltHistoryImportRecipe[])working.Clone());
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
