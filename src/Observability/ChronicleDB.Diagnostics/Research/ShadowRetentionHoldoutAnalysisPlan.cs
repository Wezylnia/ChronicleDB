using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public sealed record ShadowRetentionHoldoutAnalysisPlan
{
    public const int CurrentFormatVersion = 1;

    private static readonly IReadOnlyList<double> FrozenQuantiles = Array.AsReadOnly(new[] { 0.05d, 0.50d, 0.95d });
    private static readonly IReadOnlyList<string> FrozenPrimaryMetrics = Array.AsReadOnly(new[]
    {
        "measured-reclamation-ratio",
        "measured-released-payload-bytes",
        "thread-allocated-bytes",
        "verified-projection-milliseconds",
    });
    private static readonly IReadOnlyList<string> FrozenRequiredResultGates = Array.AsReadOnly(new[]
    {
        "candidate-subset-verified",
        "effect-model-exact",
        "flat-exact-baseline-verified",
        "observer-equivalence-verified",
        "observer-minimality-verified",
        "result-hash-and-identity-verified",
    });
    private static readonly IReadOnlyList<string> FrozenReportingRules = Array.AsReadOnly(new[]
    {
        "Analyze Holdout-A only after all preregistered Holdout-A runs complete without correctness/infrastructure failure.",
        "Do not read or execute Holdout-B unless Holdout-A is invalidated by a preregistered correctness/infrastructure failure.",
        "Exclude no successful preregistered run after observing its effect size or runtime.",
        "Report all seven case summaries separately; retain low-shadow negative controls.",
        "For each case report P05/P50/P95 for retained-payload SAR and verified projection time using the frozen quantile method.",
        "Report released logical payload bytes and thread allocation alongside SAR; maintenance runtime is not a primary speedup claim.",
        "Separate overwrite-only and tombstone-containing cases; never use the maximum tombstone ratio as a universal headline effect.",
        "Any required-result-gate, identity, result-hash, expected-release, or effect-model mismatch invalidates the partition rather than being dropped.",
    });

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string PublicationPlanSha256 { get; init; }

    public required string HoldoutExecutionPlanSha256 { get; init; }

    public required ShadowRetentionHoldoutPartition InitialPartition { get; init; }

    public required int CasesPerPartition { get; init; }

    public required int RunsPerCase { get; init; }

    public required IReadOnlyList<double> Quantiles { get; init; }

    public required string QuantileMethod { get; init; }

    public required IReadOnlyList<string> PrimaryMetrics { get; init; }

    public required IReadOnlyList<string> RequiredResultGates { get; init; }

    public required IReadOnlyList<string> NegativeControlCaseIds { get; init; }

    public required IReadOnlyList<string> ReportingRules { get; init; }

    public string SerializeCanonical()
    {
        Validate();
        return JsonSerializer.Serialize(this, CanonicalJsonOptions);
    }

    public string ComputeCanonicalSha256()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeCanonical())))
            .ToLowerInvariant();

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported A1 holdout analysis-plan format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(CandidateId)
            || !IsSha256(PublicationPlanSha256)
            || !IsSha256(HoldoutExecutionPlanSha256)
            || InitialPartition != ShadowRetentionHoldoutPartition.HoldoutA)
        {
            throw new InvalidOperationException("Holdout analysis identity/initial partition is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CasesPerPartition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RunsPerCase);
        if (Quantiles.Count != 3
            || !Quantiles.SequenceEqual(FrozenQuantiles)
            || !string.Equals(QuantileMethod, "linear-interpolation-index=(n-1)*p", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout quantiles/method are frozen to P05/P50/P95 linear interpolation.");
        }

        ValidateSortedUnique(PrimaryMetrics, nameof(PrimaryMetrics));
        ValidateSortedUnique(RequiredResultGates, nameof(RequiredResultGates));
        ValidateSortedUnique(NegativeControlCaseIds, nameof(NegativeControlCaseIds));
        if (ReportingRules.Count == 0)
        {
            throw new InvalidOperationException("Holdout reporting rules must not be empty.");
        }
    }

    public static ShadowRetentionHoldoutAnalysisPlan Create(
        ShadowRetentionPublicationPlan publicationPlan,
        ShadowRetentionHoldoutExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(publicationPlan);
        ArgumentNullException.ThrowIfNull(executionPlan);
        publicationPlan.Validate();
        executionPlan.Validate();
        var publicationHash = publicationPlan.ComputeCanonicalSha256();
        if (!string.Equals(executionPlan.PublicationPlanSha256, publicationHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout execution plan does not belong to the publication plan.");
        }

        var negativeFamilies = publicationPlan.Families
            .Where(family => family.IsNegativeControlFamily)
            .Select(family => family.FamilyId)
            .ToHashSet(StringComparer.Ordinal);
        var negativeCases = publicationPlan.HoldoutCases
            .Where(item => negativeFamilies.Contains(item.FamilyId))
            .Select(item => item.CaseId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedRunsPerCase = checked(publicationPlan.HoldoutASeeds.Count * publicationPlan.ProcessRepetitions);
        var result = new ShadowRetentionHoldoutAnalysisPlan
        {
            FormatVersion = CurrentFormatVersion,
            CandidateId = publicationPlan.CandidateId,
            PublicationPlanSha256 = publicationHash,
            HoldoutExecutionPlanSha256 = executionPlan.ComputeCanonicalSha256(),
            InitialPartition = ShadowRetentionHoldoutPartition.HoldoutA,
            CasesPerPartition = publicationPlan.HoldoutCases.Count,
            RunsPerCase = expectedRunsPerCase,
            Quantiles = FrozenQuantiles,
            QuantileMethod = "linear-interpolation-index=(n-1)*p",
            PrimaryMetrics = FrozenPrimaryMetrics,
            RequiredResultGates = FrozenRequiredResultGates,
            NegativeControlCaseIds = Array.AsReadOnly(negativeCases),
            ReportingRules = FrozenReportingRules,
        };
        result.Validate();
        return result;
    }

    private static void ValidateSortedUnique(IReadOnlyList<string> values, string name)
    {
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"{name} must be non-empty.");
        }
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count
            || !values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{name} must be unique and ordinal-sorted.");
        }
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

public static class ShadowRetentionHoldoutAnalysisPlanWriter
{
    public const string PlanFileName = "a1-shadow-holdout-analysis-plan.json";
    public const string PlanHashFileName = "a1-shadow-holdout-analysis-plan.sha256";

    public static ShadowRetentionHoldoutAnalysisPlanArtifact Write(string directoryPath, ShadowRetentionHoldoutAnalysisPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(plan);
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        var json = plan.SerializeCanonical();
        var hash = plan.ComputeCanonicalSha256();
        var planPath = Path.Combine(directory, PlanFileName);
        var hashPath = Path.Combine(directory, PlanHashFileName);
        WriteImmutable(planPath, json + Environment.NewLine);
        WriteImmutable(hashPath, hash + Environment.NewLine);
        return new ShadowRetentionHoldoutAnalysisPlanArtifact(planPath, hashPath, hash);
    }

    private static void WriteImmutable(string path, string content)
    {
        if (File.Exists(path))
        {
            if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
            {
                throw new IOException($"Holdout analysis-plan artifact already exists with different content: {path}");
            }
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

public sealed record ShadowRetentionHoldoutAnalysisPlanArtifact(string PlanPath, string HashPath, string Sha256);
