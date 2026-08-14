namespace ChronicleDB.BranchCheck;

public sealed record UnseededLocalRun(
    int RunId,
    int Seed,
    string ProfileName,
    string CandidateSetFingerprint,
    int TraceBudget,
    int FirstTargetIndex,
    string Outcome,
    CandidateSemanticClass TargetClass);

public sealed record UnseededLocalCampaignReport(
    string GrammarIdentity,
    IReadOnlyList<int> Seeds,
    int TraceBudget,
    bool ExternalEvidence,
    IReadOnlyList<UnseededLocalRun> Runs)
{
    public IReadOnlyDictionary<string, int> OutcomeCounts
        => Runs
            .GroupBy(static run => run.Outcome, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
}

/// <summary>
/// A predeclared, issue-ID-free local campaign for validating the unseeded
/// orchestration protocol. It searches uniform capability traces and uses only
/// semantic target classes as the local oracle; it is not external evidence.
/// </summary>
public static class UnseededLocalCampaign
{
    public static IReadOnlyList<int> FrozenSeeds { get; } =
    [
        39217, 39229, 39241, 39257, 39263, 39277, 39289, 39301,
        39313, 39331, 39343, 39359, 39367, 39383, 39397, 39409,
        39419, 39439, 39457, 39463, 39479, 39493, 39509, 39517,
        39529, 39541, 39551, 39563, 39577, 39581, 39593, 39607,
    ];

    public static UnseededLocalCampaignReport ExecuteDefault(int traceBudget = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(traceBudget);

        var profiles = new (string Name, BranchCapabilityProfile Profile, CandidateSemanticClass TargetClass)[]
        {
            (
                "historical-identity",
                BranchCapabilityProfile.Create("unseeded-local", supportsHistoricalFork: true),
                CandidateSemanticClass.IdentityAffecting),
            (
                "allocator-continuation",
                BranchCapabilityProfile.Create("unseeded-local"),
                CandidateSemanticClass.AllocatorAffecting),
            (
                "observer-dependency",
                BranchCapabilityProfile.Create("unseeded-local", equivalentObservers: ["primary", "alternate"]),
                CandidateSemanticClass.ObserverAffecting),
            (
                "recovery-closure",
                BranchCapabilityProfile.Create("unseeded-local", supportsRestart: true),
                CandidateSemanticClass.RecoveryAffecting),
        };

        List<UnseededLocalRun> runs = [];
        int runId = 1;
        foreach ((string name, BranchCapabilityProfile profile, CandidateSemanticClass targetClass) in profiles)
        {
            CapabilityCandidate[] candidates = CapabilityCandidateGrammar.Generate(profile).ToArray();
            int effectiveBudget = Math.Min(traceBudget, candidates.Length);
            string fingerprint = CapabilityCandidateGrammar.Fingerprint(profile);
            foreach (int seed in FrozenSeeds)
            {
                IReadOnlyList<CapabilityCandidate> ordering = CapabilityCandidateGrammar.UniformOrdering(profile, seed);
                int firstTargetIndex = Array.FindIndex(
                    ordering.ToArray(),
                    candidate => (candidate.SemanticClasses & targetClass) != 0);
                string outcome = firstTargetIndex >= 0 && firstTargetIndex < effectiveBudget
                    ? "known-failure"
                    : "no-failure";
                runs.Add(new UnseededLocalRun(
                    runId++,
                    seed,
                    name,
                    fingerprint,
                    effectiveBudget,
                    firstTargetIndex,
                    outcome,
                    targetClass));
            }
        }

        return new UnseededLocalCampaignReport(
            "capability-grammar-v1; predeclared semantic classes; uniform ordering",
            FrozenSeeds,
            traceBudget,
            ExternalEvidence: false,
            runs);
    }
}
