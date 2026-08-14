namespace ChronicleDB.BranchCheck;

public sealed record CapabilityBudgetCurvePoint(
    int Budget,
    int Runs,
    int UniformDetected,
    int GuidedDetected,
    double UniformDetectionRate,
    double GuidedDetectionRate);

public sealed record CapabilityBudgetReport(
    string Name,
    BranchCapabilityProfile Profile,
    CandidateSemanticClass TargetClasses,
    string CandidateSetFingerprint,
    IReadOnlyList<int> Seeds,
    int CandidateCount,
    IReadOnlyList<CapabilityBudgetCurvePoint> BudgetCurve)
{
    public bool GuidedHasAdvantageAtAnyBudget
        => BudgetCurve.Any(point => point.GuidedDetectionRate > point.UniformDetectionRate);
}

/// <summary>
/// Offline fair-budget calibration for capability guidance. It deliberately uses
/// semantic classes rather than historical issue identifiers and is not external
/// backend evidence.
/// </summary>
public static class CapabilityBudgetCampaign
{
    public static IReadOnlyList<int> DefaultSeeds { get; } = [1, 7, 13, 29, 61, 127, 251, 509];

    public static IReadOnlyList<CapabilityBudgetReport> ExecuteDefault()
        =>
        [
            Execute(
                "historical-identity",
                BranchCapabilityProfile.Create(
                    "capability-calibration",
                    supportsHistoricalFork: true,
                    supportsRestart: true,
                    supportsDelete: true,
                    equivalentObservers: ["current", "historical"]),
                CandidateSemanticClass.IdentityAffecting,
                DefaultSeeds),
            Execute(
                "allocator-continuation",
                BranchCapabilityProfile.Create("capability-calibration"),
                CandidateSemanticClass.AllocatorAffecting,
                DefaultSeeds),
            Execute(
                "observer-dependency",
                BranchCapabilityProfile.Create(
                    "capability-calibration",
                    supportsHistoricalFork: true,
                    equivalentObservers: ["primary", "alternate"]),
                CandidateSemanticClass.ObserverAffecting,
                DefaultSeeds),
            Execute(
                "recovery-closure",
                BranchCapabilityProfile.Create("capability-calibration", supportsRestart: true),
                CandidateSemanticClass.RecoveryAffecting,
                DefaultSeeds),
        ];

    public static CapabilityBudgetReport Execute(
        string name,
        BranchCapabilityProfile profile,
        CandidateSemanticClass targetClasses,
        IReadOnlyList<int> seeds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(seeds);
        if (targetClasses == CandidateSemanticClass.Ordinary)
        {
            throw new ArgumentException("A semantic target class is required.", nameof(targetClasses));
        }
        if (seeds.Count == 0)
        {
            throw new ArgumentException("At least one seed is required.", nameof(seeds));
        }

        CapabilityCandidate[] candidates = CapabilityCandidateGrammar.Generate(profile).ToArray();
        List<CapabilityBudgetCurvePoint> curve = [];
        for (int budget = 1; budget <= candidates.Length; budget++)
        {
            int uniformDetected = 0;
            int guidedDetected = 0;
            foreach (int seed in seeds)
            {
                if (ContainsTarget(CapabilityCandidateGrammar.UniformOrdering(profile, seed), targetClasses, budget))
                {
                    uniformDetected++;
                }
                if (ContainsTarget(CapabilityCandidateGrammar.GuidedOrdering(profile, targetClasses, seed), targetClasses, budget))
                {
                    guidedDetected++;
                }
            }

            curve.Add(new CapabilityBudgetCurvePoint(
                budget,
                seeds.Count,
                uniformDetected,
                guidedDetected,
                (double)uniformDetected / seeds.Count,
                (double)guidedDetected / seeds.Count));
        }

        return new CapabilityBudgetReport(
            name,
            profile,
            targetClasses,
            CapabilityCandidateGrammar.Fingerprint(profile),
            seeds.ToArray(),
            candidates.Length,
            curve);
    }

    private static bool ContainsTarget(
        IReadOnlyList<CapabilityCandidate> ordering,
        CandidateSemanticClass targetClasses,
        int budget)
        => ordering
            .Take(budget)
            .Any(candidate => (candidate.SemanticClasses & targetClasses) != 0);
}
