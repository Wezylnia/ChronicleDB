namespace ChronicleDB.BranchCheck;

public sealed record TriggerRecipeEvidence(
    MatrixOneIdentityMutationRecipe Recipe,
    bool IdentityStateRelevant,
    RelationStatus BoundaryRelation,
    BaselineStatus GenericStateBaseline,
    BaselineStatus BranchGrammarBaseline,
    bool TriggeredBoundaryViolation);

public sealed record TriggerBudgetPoint(
    int CandidateBudget,
    double GenericDetectionRate,
    double RelationGuidedDetectionRate);

public sealed record TriggerBudgetReport(
    IReadOnlyList<TriggerRecipeEvidence> Recipes,
    IReadOnlyList<TriggerBudgetPoint> BudgetCurve,
    int CandidateCount,
    int IdentityRelevantRecipeCount,
    int ViolationRecipeCount,
    bool AllViolationsInsideIdentityRelevantClass,
    bool GuidedHasStrictAdvantageAtAnyBudget,
    string CandidateSetFingerprint,
    string FairnessNote);

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
                MatrixOneIdentityMutationSemantics.IsIdentityStateRelevant(recipe),
                boundary.Status,
                genericState.Status,
                branchGrammar.Status,
                boundary.Status == RelationStatus.Fail));
        }

        return EvaluateEvidence(evidence);
    }

    public static TriggerBudgetReport EvaluateEvidence(IReadOnlyList<TriggerRecipeEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0 || evidence.Select(static item => item.Recipe).Distinct().Count() != evidence.Count)
        {
            throw new ArgumentException("MatrixOne budget evidence must contain unique recipes.", nameof(evidence));
        }

        int candidateCount = evidence.Count;
        int relevantCount = evidence.Count(static item => item.IdentityStateRelevant);
        int violationCount = evidence.Count(static item => item.TriggeredBoundaryViolation);
        int relevantViolations = evidence.Count(static item => item.IdentityStateRelevant && item.TriggeredBoundaryViolation);
        int controlViolations = violationCount - relevantViolations;
        bool allViolationsInsideRelevant = controlViolations == 0;

        var curve = new List<TriggerBudgetPoint>(candidateCount);
        for (int budget = 1; budget <= candidateCount; budget++)
        {
            double generic = DetectionRate(candidateCount, violationCount, budget);
            double guided = GroupPrioritizedDetectionRate(
                relevantCount,
                candidateCount - relevantCount,
                relevantViolations,
                controlViolations,
                budget);
            curve.Add(new TriggerBudgetPoint(budget, generic, guided));
        }

        return new TriggerBudgetReport(
            evidence.ToArray(),
            curve,
            candidateCount,
            relevantCount,
            violationCount,
            allViolationsInsideRelevant,
            curve.Any(static point => point.RelationGuidedDetectionRate > point.GenericDetectionRate),
            MatrixOneIdentityMutationSemantics.Fingerprint(),
            "Relation-guided search prioritizes the complete source-identity-risk class and treats every ordering within that class uniformly; it never names or selects an exact historically failing recipe.");
    }

    public static double DetectionRate(int candidateCount, int violationCount, int budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(violationCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(violationCount, candidateCount);
        if (budget < 1 || budget > candidateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        if (violationCount == 0)
        {
            return 0.0;
        }
        if (budget > candidateCount - violationCount)
        {
            return 1.0;
        }

        return 1.0 - CombinationRatio(candidateCount - violationCount, candidateCount, budget);
    }

    public static double GroupPrioritizedDetectionRate(
        int relevantCount,
        int controlCount,
        int relevantViolationCount,
        int controlViolationCount,
        int budget)
    {
        if (relevantCount < 0 || controlCount < 0 || relevantViolationCount < 0 || controlViolationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(relevantCount));
        }
        if (relevantViolationCount > relevantCount || controlViolationCount > controlCount)
        {
            throw new ArgumentOutOfRangeException(nameof(relevantViolationCount));
        }
        int total = relevantCount + controlCount;
        if (total == 0 || budget < 1 || budget > total)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        if (budget <= relevantCount)
        {
            return DetectionRate(relevantCount, relevantViolationCount, budget);
        }
        if (relevantViolationCount > 0)
        {
            return 1.0;
        }

        return DetectionRate(controlCount, controlViolationCount, budget - relevantCount);
    }

    private static double CombinationRatio(int safeItems, int totalItems, int draws)
    {
        double ratio = 1.0;
        for (int index = 0; index < draws; index++)
        {
            ratio *= (safeItems - index) / (double)(totalItems - index);
        }
        return ratio;
    }
}
