using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.BranchCheck;

public enum ExternalUnseededOutcome
{
    NoFailure,
    KnownFailure,
    DuplicateRootCause,
    NewRootCauseCandidate,
    FalsePositive,
    OracleAmbiguity,
    HarnessEnvironmentFailure,
}

public sealed record ExternalUnseededCandidate(
    string Family,
    string Version,
    string Candidate,
    string SemanticClass,
    bool SemanticRelevant,
    bool Violation,
    bool GenericBaselineDetected,
    string RelationId,
    string RootCauseSignature,
    string? EvidenceDetail);

public sealed record ExternalUnseededRun(
    int RunId,
    int Seed,
    string Family,
    string Version,
    string CandidateSetFingerprint,
    int TraceBudget,
    IReadOnlyList<string> CandidateOrder,
    int FirstFailureIndex,
    string Outcome,
    string RelationId,
    string RootCauseSignature,
    string EvidenceProvenance);

public sealed record ExternalUnseededCampaignReport(
    string GrammarIdentity,
    IReadOnlyList<int> Seeds,
    int TraceBudget,
    int TimeBudgetMilliseconds,
    bool ExternalEvidence,
    bool ReplayFromFrozenCandidateObservations,
    bool LiveBackendReruns,
    IReadOnlyList<ExternalUnseededCandidate> Candidates,
    IReadOnlyList<ExternalUnseededRun> Runs)
{
    public IReadOnlyDictionary<string, int> OutcomeCounts
        => Enum.GetNames<ExternalUnseededOutcome>()
            .ToDictionary(
                name => name,
                name => Runs.Count(run => string.Equals(run.Outcome, name, StringComparison.Ordinal)),
                StringComparer.Ordinal);
}

/// <summary>
/// Replays complete per-candidate observations from independently executed
/// MatrixOne, Dolt, and SlateDB artifacts under a preregistered uniform order.
/// The backend observations are external evidence; the seed replay is deliberately
/// labelled as a replay because it does not rerun a backend for every permutation.
/// </summary>
public static class ExternalUnseededCampaign
{
    public static IReadOnlyList<int> FrozenSeeds { get; } =
    [
        51001, 51017, 51029, 51047, 51061, 51073, 51089, 51107,
        51121, 51137, 51151, 51169, 51193, 51203, 51217, 51229,
        51241, 51263, 51283, 51287, 51307, 51317, 51329, 51341,
        51349, 51361, 51373, 51383, 51397, 51407, 51419, 51437,
    ];

    public static ExternalUnseededCampaignReport ExecuteFromFrozenArtifacts(
        string matrixOneBudgetPath,
        string dolt223Path,
        string dolt230Path,
        string slateBuggyPath,
        string slateFixedPath,
        int traceBudget = 4,
        int timeBudgetMilliseconds = 120_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixOneBudgetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dolt223Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(dolt230Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(slateBuggyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(slateFixedPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(traceBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeBudgetMilliseconds);

        List<ExternalUnseededCandidate> candidates = [];
        AddMatrixOne(candidates, matrixOneBudgetPath);
        AddDolt(candidates, dolt223Path, "Dolt 2.2.3");
        AddDolt(candidates, dolt230Path, "Dolt 2.3.0");
        AddSlate(candidates, slateBuggyPath, "SlateDB 0.14.1");
        AddSlate(candidates, slateFixedPath, "SlateDB fix 6a131a9e");

        List<ExternalUnseededRun> runs = [];
        int runId = 1;
        HashSet<string> observedRootCauses = new(StringComparer.Ordinal);
        foreach (IGrouping<(string Family, string Version), ExternalUnseededCandidate> group in candidates.GroupBy(
                     candidate => (candidate.Family, candidate.Version)))
        {
            ExternalUnseededCandidate[] familyCandidates = group.ToArray();
            string fingerprint = Fingerprint(familyCandidates);
            foreach (int seed in FrozenSeeds)
            {
                ExternalUnseededCandidate[] ordering = UniformOrdering(familyCandidates, seed);
                int effectiveBudget = Math.Min(traceBudget, ordering.Length);
                int firstFailureIndex = Array.FindIndex(ordering, candidate => candidate.Violation);
                ExternalUnseededCandidate? firstFailure = firstFailureIndex >= 0 && firstFailureIndex < effectiveBudget
                    ? ordering[firstFailureIndex]
                    : null;
                ExternalUnseededOutcome outcome = Classify(firstFailure, firstFailureIndex, observedRootCauses);
                runs.Add(new ExternalUnseededRun(
                    runId++,
                    seed,
                    group.Key.Family,
                    group.Key.Version,
                    fingerprint,
                    effectiveBudget,
                    ordering.Select(candidate => candidate.Candidate).ToArray(),
                    firstFailureIndex,
                    outcome.ToString(),
                    firstFailure?.RelationId ?? "none",
                    firstFailure?.RootCauseSignature ?? "none",
                    "frozen per-candidate observation; deterministic uniform replay"));
                if (firstFailure is not null && firstFailure.SemanticRelevant)
                {
                    observedRootCauses.Add(firstFailure.RootCauseSignature);
                }
            }
        }

        return new ExternalUnseededCampaignReport(
            "external-unseeded-v1;five frozen external versions;uniform Fisher-Yates order",
            FrozenSeeds,
            traceBudget,
            timeBudgetMilliseconds,
            ExternalEvidence: true,
            ReplayFromFrozenCandidateObservations: true,
            LiveBackendReruns: false,
            candidates,
            runs);
    }

    private static ExternalUnseededOutcome Classify(
        ExternalUnseededCandidate? failure,
        int failureIndex,
        HashSet<string> observedRootCauses)
    {
        if (failure is null || failureIndex < 0)
        {
            return ExternalUnseededOutcome.NoFailure;
        }

        if (!failure.SemanticRelevant)
        {
            return ExternalUnseededOutcome.FalsePositive;
        }

        if (observedRootCauses.Contains(failure.RootCauseSignature))
        {
            return ExternalUnseededOutcome.DuplicateRootCause;
        }

        return failure.RootCauseSignature switch
        {
            "MatrixOne:BC.temporal-boundary"
                or "Dolt:BC.continuation-state"
                or "SlateDB:BC.observer-dependency" => ExternalUnseededOutcome.KnownFailure,
            _ => ExternalUnseededOutcome.NewRootCauseCandidate,
        };
    }

    private static void AddMatrixOne(List<ExternalUnseededCandidate> candidates, string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement report = document.RootElement.GetProperty("Report");
        foreach (JsonElement recipe in report.GetProperty("Recipes").EnumerateArray())
        {
            bool relevant = recipe.GetProperty("IdentityStateRelevant").GetBoolean();
            bool violation = recipe.GetProperty("TriggeredBoundaryViolation").GetBoolean();
            candidates.Add(new ExternalUnseededCandidate(
                "MatrixOne identity",
                "MatrixOne v4.1.4 OCI c920128b",
                recipe.GetProperty("Recipe").GetString() ?? "",
                relevant ? "identity" : "ordinary/control",
                relevant,
                violation,
                string.Equals(recipe.GetProperty("GenericStateBaseline").GetString(), "Detected", StringComparison.Ordinal),
                "BC.temporal-boundary",
                relevant ? "MatrixOne:BC.temporal-boundary" : "MatrixOne:generic-oracle-mismatch",
                null));
        }
    }

    private static void AddDolt(List<ExternalUnseededCandidate> candidates, string path, string version)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement report = document.RootElement.GetProperty("Report");
        foreach (JsonElement recipe in report.GetProperty("Recipes").EnumerateArray())
        {
            bool relevant = recipe.GetProperty("SequenceStateRelevant").GetBoolean();
            bool violation = recipe.GetProperty("TriggeredViolation").GetBoolean();
            candidates.Add(new ExternalUnseededCandidate(
                "Dolt history import",
                version,
                recipe.GetProperty("Recipe").GetString() ?? "",
                relevant ? "sequence-state" : "observer/control",
                relevant,
                violation,
                string.Equals(recipe.GetProperty("GenericStateBaseline").GetString(), "Detected", StringComparison.Ordinal),
                "BC.continuation-state",
                relevant ? "Dolt:BC.continuation-state" : "Dolt:control-model-mismatch",
                recipe.GetProperty("ContinuationRelationEvidence").GetString()));
        }
    }

    private static void AddSlate(List<ExternalUnseededCandidate> candidates, string path, string version)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement report = document.RootElement.GetProperty("Report");
        foreach (JsonElement candidate in report.GetProperty("Candidates").EnumerateArray())
        {
            bool relevant = candidate.GetProperty("DependencyRelevant").GetBoolean();
            bool violation = candidate.GetProperty("ViolatesExpectedReadability").GetBoolean();
            candidates.Add(new ExternalUnseededCandidate(
                "SlateDB observer dependency",
                version,
                candidate.GetProperty("Candidate").GetString() ?? "",
                relevant ? "observer-dependency" : "observer/control",
                relevant,
                violation,
                false,
                "BC.observer-dependency",
                relevant ? "SlateDB:BC.observer-dependency" : "SlateDB:control-model-mismatch",
                candidate.TryGetProperty("Error", out JsonElement error) && error.ValueKind != JsonValueKind.Null
                    ? error.GetString()
                    : null));
        }
    }

    private static ExternalUnseededCandidate[] UniformOrdering(
        IReadOnlyList<ExternalUnseededCandidate> source,
        int seed)
    {
        ExternalUnseededCandidate[] ordering = source.ToArray();
        Random random = new(seed);
        for (int i = ordering.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (ordering[i], ordering[j]) = (ordering[j], ordering[i]);
        }

        return ordering;
    }

    private static string Fingerprint(IEnumerable<ExternalUnseededCandidate> candidates)
    {
        string canonical = string.Join(
            '\n',
            candidates.Select(candidate => string.Join(
                '|',
                candidate.Candidate,
                candidate.SemanticClass,
                candidate.SemanticRelevant,
                candidate.RelationId)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
