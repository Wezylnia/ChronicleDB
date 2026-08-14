namespace ChronicleDB.BranchCheck;

public sealed record DoltTriggerRecipeEvidence(
    DoltHistoryImportRecipe Recipe,
    bool SequenceStateRelevant,
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
    int SequenceRelevantRecipeCount,
    bool AllViolationsInsideSequenceRelevantClass,
    bool GuidedHasStrictAdvantageAtAnyBudget,
    string PortabilityNote);

public static class DoltTriggerBudgetCampaign
{
    // Cross-version common denominator for the paired 2.2.3/2.3.0 provider experiment.
    // FetchHardReset remains a valid diagnostic recipe, but Dolt 2.3.0 cancels the
    // calling SQL context during DOLT_RESET --hard. It is therefore excluded from
    // the paired budget rather than being misclassified as a semantic violation.
    public static IReadOnlyList<DoltHistoryImportRecipe> PortableRecipes { get; } =
    [
        DoltHistoryImportRecipe.NoOp,
        DoltHistoryImportRecipe.FetchOnly,
        DoltHistoryImportRecipe.Pull,
        DoltHistoryImportRecipe.FetchMerge,
    ];

    public static async Task<DoltTriggerBudgetReport> ExecuteAsync(
        DoltCliOptions options,
        CancellationToken cancellationToken = default)
    {
        DoltHistoryImportRecipe[] recipes = PortableRecipes.ToArray();
        var evidence = new List<DoltTriggerRecipeEvidence>(recipes.Length);
        string backendVersion = "unknown";
        foreach (DoltHistoryImportRecipe recipe in recipes)
        {
            BranchScenario scenario = await DoltSqlServerHistoryImportAdapter.ExecuteAsync(
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
                DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe),
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
        int relevantCount = evidence.Count(static item => item.SequenceStateRelevant);
        bool allViolationsInsideRelevant = evidence
            .Where(static item => item.TriggeredViolation)
            .All(static item => item.SequenceStateRelevant);
        bool strictAdvantage = curve.Any(static point => point.GuidedDetectionRate > point.GenericDetectionRate);
        return new DoltTriggerBudgetReport(
            backendVersion,
            evidence.ToArray(),
            curve,
            violationCount,
            relevantCount,
            allViolationsInsideRelevant,
            strictAdvantage,
            "Portable paired budget excludes FetchHardReset because Dolt 2.3.0 cancels the SQL caller context during DOLT_RESET --hard; that recipe is not counted as a failure or success in the paired curve.");
    }

    public static IReadOnlyList<DoltHistoryImportRecipe[]> GenerateRiskPrioritizedOrderings(
        IReadOnlyList<DoltTriggerRecipeEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        DoltHistoryImportRecipe[] relevant = evidence
            .Where(static item => item.SequenceStateRelevant)
            .Select(static item => item.Recipe)
            .ToArray();
        DoltHistoryImportRecipe[] controls = evidence
            .Where(static item => !item.SequenceStateRelevant)
            .Select(static item => item.Recipe)
            .ToArray();

        IReadOnlyList<DoltHistoryImportRecipe[]> relevantOrderings = GeneratePermutations(relevant);
        IReadOnlyList<DoltHistoryImportRecipe[]> controlOrderings = GeneratePermutations(controls);
        var combined = new List<DoltHistoryImportRecipe[]>(Math.Max(1, relevantOrderings.Count) * Math.Max(1, controlOrderings.Count));
        foreach (DoltHistoryImportRecipe[] high in relevantOrderings.DefaultIfEmpty([]))
        {
            foreach (DoltHistoryImportRecipe[] low in controlOrderings.DefaultIfEmpty([]))
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
