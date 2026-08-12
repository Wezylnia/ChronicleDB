using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public enum ShadowRetentionHoldoutPartition : byte
{
    HoldoutA = 1,
    HoldoutB = 2,
}

public sealed record ShadowRetentionHoldoutExecutionPlan
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string PublicationPlanSha256 { get; init; }

    public required IReadOnlyList<ShadowRetentionHoldoutRunSpec> Runs { get; init; }

    public int HoldoutARunCount => Runs.Count(run => run.Partition == ShadowRetentionHoldoutPartition.HoldoutA);

    public int HoldoutBRunCount => Runs.Count(run => run.Partition == ShadowRetentionHoldoutPartition.HoldoutB);

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
                $"Unsupported A1 holdout execution-plan format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(CandidateId) || !IsSha256(PublicationPlanSha256))
        {
            throw new InvalidOperationException("Holdout execution plan requires candidate identity and publication SHA-256.");
        }

        if (Runs.Count == 0 || Runs.Select(run => run.RunId).Distinct(StringComparer.Ordinal).Count() != Runs.Count)
        {
            throw new InvalidOperationException("Holdout run identities must be non-empty and unique.");
        }

        foreach (var run in Runs)
        {
            run.Validate();
        }

        var canonical = Runs
            .OrderBy(run => run.Partition)
            .ThenBy(run => run.TrialOrder)
            .Select(run => run.RunId)
            .ToArray();
        if (!Runs.Select(run => run.RunId).SequenceEqual(canonical, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Holdout runs must be serialized in partition/trial order.");
        }

        foreach (var partition in Enum.GetValues<ShadowRetentionHoldoutPartition>())
        {
            var partitionRuns = Runs.Where(run => run.Partition == partition).ToArray();
            if (!partitionRuns.Select(run => run.TrialOrder).SequenceEqual(Enumerable.Range(0, partitionRuns.Length)))
            {
                throw new InvalidOperationException($"{partition} trial order must be contiguous from zero.");
            }

            if (!partitionRuns.Select(run => run.OrderKeySha256)
                    .SequenceEqual(partitionRuns.Select(run => run.OrderKeySha256).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"{partition} runs must follow deterministic order keys.");
            }
        }
    }

    public static ShadowRetentionHoldoutExecutionPlan Create(ShadowRetentionPublicationPlan publicationPlan)
    {
        ArgumentNullException.ThrowIfNull(publicationPlan);
        publicationPlan.Validate();
        var publicationHash = publicationPlan.ComputeCanonicalSha256();
        var families = publicationPlan.Families.ToDictionary(family => family.FamilyId, StringComparer.Ordinal);
        var raw = new List<RawRun>();
        AddPartition(raw, publicationPlan, families, ShadowRetentionHoldoutPartition.HoldoutA, publicationPlan.HoldoutASeeds);
        AddPartition(raw, publicationPlan, families, ShadowRetentionHoldoutPartition.HoldoutB, publicationPlan.HoldoutBSeeds);

        var runs = new List<ShadowRetentionHoldoutRunSpec>(raw.Count);
        foreach (var partition in Enum.GetValues<ShadowRetentionHoldoutPartition>())
        {
            var ordered = raw
                .Where(run => run.Partition == partition)
                .Select(run => (Run: run, OrderKey: ComputeOrderKey(publicationHash, run)))
                .OrderBy(item => item.OrderKey, StringComparer.Ordinal)
                .ToArray();
            for (var trialOrder = 0; trialOrder < ordered.Length; trialOrder++)
            {
                var item = ordered[trialOrder];
                runs.Add(new ShadowRetentionHoldoutRunSpec
                {
                    RunId = FormattableString.Invariant(
                        $"{partition}:{item.Run.CaseId}:seed={item.Run.Seed}:rep={item.Run.ProcessRepetition}"),
                    Partition = partition,
                    TrialOrder = trialOrder,
                    OrderKeySha256 = item.OrderKey,
                    CaseId = item.Run.CaseId,
                    FamilyId = item.Run.FamilyId,
                    TopologyKind = item.Run.TopologyKind,
                    BranchCountOrDepth = item.Run.BranchCountOrDepth,
                    ShadowFraction = item.Run.ShadowFraction,
                    TombstoneFraction = item.Run.TombstoneFraction,
                    KeyCount = item.Run.KeyCount,
                    Seed = item.Run.Seed,
                    ProcessRepetition = item.Run.ProcessRepetition,
                });
            }
        }

        var result = new ShadowRetentionHoldoutExecutionPlan
        {
            FormatVersion = CurrentFormatVersion,
            CandidateId = publicationPlan.CandidateId,
            PublicationPlanSha256 = publicationHash,
            Runs = runs.AsReadOnly(),
        };
        result.Validate();
        return result;
    }

    private static void AddPartition(
        ICollection<RawRun> runs,
        ShadowRetentionPublicationPlan publicationPlan,
        Dictionary<string, ShadowRetentionPublicationFamily> families,
        ShadowRetentionHoldoutPartition partition,
        IReadOnlyList<int> seeds)
    {
        foreach (var selectedCase in publicationPlan.HoldoutCases)
        {
            var family = families[selectedCase.FamilyId];
            foreach (var seed in seeds)
            {
                for (var repetition = 0; repetition < publicationPlan.ProcessRepetitions; repetition++)
                {
                    runs.Add(new RawRun(
                        partition,
                        selectedCase.CaseId,
                        selectedCase.FamilyId,
                        family.TopologyKind,
                        selectedCase.BranchCountOrDepth,
                        selectedCase.ShadowFraction,
                        selectedCase.TombstoneFraction,
                        selectedCase.KeyCount,
                        seed,
                        repetition));
                }
            }
        }
    }

    private static string ComputeOrderKey(string publicationHash, RawRun run)
    {
        var identity = string.Join(
            '|',
            publicationHash,
            run.Partition,
            run.CaseId,
            run.FamilyId,
            run.TopologyKind,
            run.BranchCountOrDepth.ToString(CultureInfo.InvariantCulture),
            BitConverter.DoubleToInt64Bits(run.ShadowFraction).ToString("x16", CultureInfo.InvariantCulture),
            BitConverter.DoubleToInt64Bits(run.TombstoneFraction).ToString("x16", CultureInfo.InvariantCulture),
            run.KeyCount.ToString(CultureInfo.InvariantCulture),
            run.Seed.ToString(CultureInfo.InvariantCulture),
            run.ProcessRepetition.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record RawRun(
        ShadowRetentionHoldoutPartition Partition,
        string CaseId,
        string FamilyId,
        string TopologyKind,
        int BranchCountOrDepth,
        double ShadowFraction,
        double TombstoneFraction,
        int KeyCount,
        int Seed,
        int ProcessRepetition);
}

public sealed record ShadowRetentionHoldoutRunSpec
{
    public required string RunId { get; init; }

    public required ShadowRetentionHoldoutPartition Partition { get; init; }

    public required int TrialOrder { get; init; }

    public required string OrderKeySha256 { get; init; }

    public required string CaseId { get; init; }

    public required string FamilyId { get; init; }

    public required string TopologyKind { get; init; }

    public required int BranchCountOrDepth { get; init; }

    public required double ShadowFraction { get; init; }

    public required double TombstoneFraction { get; init; }

    public required int KeyCount { get; init; }

    public required int Seed { get; init; }

    public required int ProcessRepetition { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { RunId, OrderKeySha256, CaseId, FamilyId, TopologyKind })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Holdout run identity fields must not be empty.");
            }
        }

        if (!Enum.IsDefined(Partition) || OrderKeySha256.Length != 64 || OrderKeySha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException("Holdout partition/order key is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(TrialOrder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BranchCountOrDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(KeyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Seed);
        ArgumentOutOfRangeException.ThrowIfNegative(ProcessRepetition);
        if (!double.IsFinite(ShadowFraction) || ShadowFraction is < 0d or > 1d
            || !double.IsFinite(TombstoneFraction) || TombstoneFraction is < 0d or > 1d)
        {
            throw new InvalidOperationException("Holdout shadow/tombstone fractions must be finite [0,1].");
        }
    }
}

public static class ShadowRetentionHoldoutExecutionPlanWriter
{
    public const string PlanFileName = "a1-shadow-holdout-execution-plan.json";
    public const string PlanHashFileName = "a1-shadow-holdout-execution-plan.sha256";

    public static ShadowRetentionHoldoutExecutionPlanArtifact Write(string directoryPath, ShadowRetentionHoldoutExecutionPlan plan)
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
        return new ShadowRetentionHoldoutExecutionPlanArtifact(planPath, hashPath, hash);
    }

    private static void WriteImmutable(string path, string content)
    {
        if (File.Exists(path))
        {
            if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
            {
                throw new IOException($"Holdout execution-plan artifact already exists with different content: {path}");
            }
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

public sealed record ShadowRetentionHoldoutExecutionPlanArtifact(string PlanPath, string HashPath, string Sha256);
