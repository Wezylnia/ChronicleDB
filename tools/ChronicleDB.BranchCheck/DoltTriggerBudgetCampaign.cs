using System.Security.Cryptography;
using System.Text;

namespace ChronicleDB.BranchCheck;

public sealed record DoltTriggerRecipeEvidence(
    DoltHistoryImportRecipe Recipe,
    bool SequenceStateRelevant,
    RelationStatus ContinuationRelation,
    string ContinuationRelationEvidence,
    OutcomeClass BranchContinuationOutcome,
    OutcomeClass ReferenceContinuationOutcome,
    string? ActualContinuationToken,
    string? ExpectedContinuationToken,
    string BranchContinuationState,
    string ReferenceContinuationState,
    string? ContinuationDetail,
    BaselineStatus GenericStateBaseline,
    BaselineStatus BranchGrammarBaseline,
    bool TriggeredViolation);

public sealed record DoltTriggerBudgetPoint(
    int CandidateBudget,
    long GenericOrderings,
    long GenericOrderingsDetected,
    double GenericDetectionRate,
    long GuidedOrderings,
    long GuidedOrderingsDetected,
    double GuidedDetectionRate);

public sealed record DoltTriggerBudgetReport(
    string BackendVersion,
    IReadOnlyList<DoltTriggerRecipeEvidence> Recipes,
    IReadOnlyList<DoltTriggerBudgetPoint> BudgetCurve,
    int ViolationRecipeCount,
    int SequenceRelevantRecipeCount,
    bool AllViolationsInsideSequenceRelevantClass,
    bool GuidedHasStrictAdvantageAtAnyBudget,
    string PortabilityNote,
    string CandidateSetFingerprint,
    string FairnessNote);

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

    // Frozen before the expanded external run. The first four recipes are
    // relation-agnostic controls; the remaining six import or refresh remote
    // history through distinct legal portable sequences. No recipe names an
    // issue, table, or historically failing operation.
    public static IReadOnlyList<DoltHistoryImportRecipe> ExpandedPortableRecipes { get; } =
    [
        DoltHistoryImportRecipe.NoOp,
        DoltHistoryImportRecipe.StatusOnly,
        DoltHistoryImportRecipe.BranchList,
        DoltHistoryImportRecipe.LogLocal,
        DoltHistoryImportRecipe.FetchOnly,
        DoltHistoryImportRecipe.FetchThenStatus,
        DoltHistoryImportRecipe.FetchThenBranchList,
        DoltHistoryImportRecipe.FetchThenLog,
        DoltHistoryImportRecipe.FetchMerge,
        DoltHistoryImportRecipe.Pull,
    ];

    public static string ExpandedCandidateSetFingerprint { get; } = Fingerprint(ExpandedPortableRecipes);

    public static async Task<DoltTriggerBudgetReport> ExecuteAsync(
        DoltCliOptions options,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(options, PortableRecipes, cancellationToken).ConfigureAwait(false);

    public static async Task<DoltTriggerBudgetReport> ExecuteAsync(
        DoltCliOptions options,
        IReadOnlyList<DoltHistoryImportRecipe> recipes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recipes);
        DoltHistoryImportRecipe[] candidateRecipes = recipes.ToArray();
        if (candidateRecipes.Length == 0 || candidateRecipes.Distinct().Count() != candidateRecipes.Length)
        {
            throw new ArgumentException("Dolt candidate grammar must contain unique recipes.", nameof(recipes));
        }

        var evidence = new List<DoltTriggerRecipeEvidence>(candidateRecipes.Length);
        string backendVersion = "unknown";
        foreach (DoltHistoryImportRecipe recipe in candidateRecipes)
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
            TraceFrame continuationFrame = scenario.Frames.Single(static frame =>
                string.Equals(frame.Operation, "continuation", StringComparison.Ordinal));
            evidence.Add(new DoltTriggerRecipeEvidence(
                recipe,
                DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe),
                continuation.Status,
                continuation.Evidence,
                continuationFrame.Branch.Outcome,
                continuationFrame.Reference.Outcome,
                continuationFrame.Branch.State?.ContinuationToken,
                continuationFrame.Reference.State?.ContinuationToken,
                DescribeState(continuationFrame.Branch.State),
                DescribeState(continuationFrame.Reference.State),
                continuationFrame.Branch.Detail,
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

        if (recipes.Length > 6)
        {
            return EvaluateAnalyticEvidence(backendVersion, evidence);
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
            "Portable paired budget excludes FetchHardReset because Dolt 2.3.0 cancels the SQL caller context during DOLT_RESET --hard; that recipe is not counted as a failure or success in the paired curve.",
            Fingerprint(recipes),
            "Legacy four-recipe paired budget; retained for historical comparison and not used as the expanded fair-search headline.");
    }

    private static DoltTriggerBudgetReport EvaluateAnalyticEvidence(
        string backendVersion,
        IReadOnlyList<DoltTriggerRecipeEvidence> evidence)
    {
        DoltHistoryImportRecipe[] recipes = evidence.Select(static item => item.Recipe).ToArray();
        int candidateCount = recipes.Length;
        int violationCount = evidence.Count(static item => item.TriggeredViolation);
        int relevantCount = evidence.Count(static item => item.SequenceStateRelevant);
        int relevantViolations = evidence.Count(static item => item.SequenceStateRelevant && item.TriggeredViolation);
        int controlCount = candidateCount - relevantCount;
        int controlViolations = violationCount - relevantViolations;
        var curve = new List<DoltTriggerBudgetPoint>(candidateCount);
        long genericOrderings = Factorial(candidateCount);
        long guidedOrderings = checked(Factorial(relevantCount) * Factorial(controlCount));

        for (int budget = 1; budget <= candidateCount; budget++)
        {
            long genericWithoutViolation = PrefixSafeOrderingCount(
                candidateCount - violationCount,
                candidateCount,
                budget);
            long genericDetected = genericOrderings - genericWithoutViolation;
            long guidedWithoutViolation = GuidedSafeOrderingCount(
                relevantCount,
                controlCount,
                relevantViolations,
                controlViolations,
                budget);
            long guidedDetected = guidedOrderings - guidedWithoutViolation;
            curve.Add(new DoltTriggerBudgetPoint(
                budget,
                genericOrderings,
                genericDetected,
                genericDetected / (double)genericOrderings,
                guidedOrderings,
                guidedDetected,
                guidedDetected / (double)guidedOrderings));
        }

        return new DoltTriggerBudgetReport(
            backendVersion,
            evidence.ToArray(),
            curve,
            violationCount,
            relevantCount,
            evidence.Where(static item => item.TriggeredViolation).All(static item => item.SequenceStateRelevant),
            curve.Any(static point => point.GuidedDetectionRate > point.GenericDetectionRate),
            "Expanded ten-recipe grammar is frozen before execution. Guidance prioritizes the complete sequence-state-relevant class and never selects an exact known failing recipe.",
            Fingerprint(recipes),
            "Expanded fair-search campaign: four observer controls and six legal history-import sequences; no issue-specific recipe selector.");
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

    private static string DescribeState(CanonicalState? state)
    {
        if (state is null)
        {
            return "<null>";
        }

        string values = string.Join(
            ";",
            state.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => pair.Key + "=" + pair.Value));
        return values + ";continuation=" + (state.ContinuationToken ?? "<null>");
    }

    private static int CountDetected(
        IReadOnlyList<DoltHistoryImportRecipe[]> orderings,
        IReadOnlyList<DoltTriggerRecipeEvidence> evidence,
        int budget)
        => orderings.Count(ordering =>
            ordering.Take(budget).Any(recipe => evidence.Single(item => item.Recipe == recipe).TriggeredViolation));

    private static long GuidedSafeOrderingCount(
        int relevantCount,
        int controlCount,
        int relevantViolations,
        int controlViolations,
        int budget)
    {
        long relevantPermutations = Factorial(relevantCount);
        long controlPermutations = Factorial(controlCount);
        if (budget <= relevantCount)
        {
            return checked(
                FallingFactorial(relevantCount - relevantViolations, budget)
                * Factorial(relevantCount - budget)
                * controlPermutations);
        }

        if (relevantViolations > 0)
        {
            return 0;
        }

        int controlBudget = budget - relevantCount;
        return checked(
            relevantPermutations
            * FallingFactorial(controlCount - controlViolations, controlBudget)
            * Factorial(controlCount - controlBudget));
    }

    private static long PrefixSafeOrderingCount(int safeCount, int totalCount, int budget)
        => checked(FallingFactorial(safeCount, budget) * Factorial(totalCount - budget));

    private static long FallingFactorial(int value, int count)
    {
        if (count < 0 || count > value)
        {
            return 0;
        }

        long result = 1;
        for (int index = 0; index < count; index++)
        {
            result = checked(result * (value - index));
        }
        return result;
    }

    private static long Factorial(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        long result = 1;
        for (int index = 2; index <= value; index++)
        {
            result = checked(result * index);
        }
        return result;
    }

    private static string Fingerprint(IReadOnlyList<DoltHistoryImportRecipe> recipes)
    {
        string canonical = string.Join(
            "|",
            recipes.Select(recipe =>
                $"{recipe}:{DoltHistoryImportSemantics.ChangesCurrentVisibleRows(recipe)}:{DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

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
