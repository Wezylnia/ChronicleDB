using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Immutable, result-blind execution order for A1 Pilot-A. The full one-shot
/// sensitivity sweep and the process-repeated sentinel cases are both derived
/// exclusively from the sealed publication plan before any Pilot-A result exists.
/// </summary>
public sealed record ShadowRetentionPilotExecutionPlan
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string PublicationPlanSha256 { get; init; }

    public required IReadOnlyList<ShadowRetentionPilotRunSpec> Runs { get; init; }

    public int SweepRunCount => Runs.Count(run => run.Tier == ShadowRetentionPilotTier.SensitivitySweep);

    public int RepeatedRunCount => Runs.Count(run => run.Tier == ShadowRetentionPilotTier.RepeatedSentinel);

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
                $"Unsupported A1 Pilot-A execution-plan format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(CandidateId)
            || !IsSha256(PublicationPlanSha256)
            || Runs.Count == 0)
        {
            throw new InvalidOperationException("Pilot-A execution plan requires candidate, publication-plan hash and runs.");
        }

        if (Runs.Select(run => run.RunId).Distinct(StringComparer.Ordinal).Count() != Runs.Count
            || Runs.Select(run => run.OrderKeySha256).Distinct(StringComparer.Ordinal).Count() != Runs.Count)
        {
            throw new InvalidOperationException("Pilot-A execution runs require unique run IDs and order keys.");
        }

        for (var index = 0; index < Runs.Count; index++)
        {
            var run = Runs[index];
            run.Validate();
            if (run.TrialOrder != index)
            {
                throw new InvalidOperationException("Pilot-A TrialOrder must be contiguous and match serialized run order.");
            }
        }

        if (!Runs.Select(run => run.OrderKeySha256)
                .SequenceEqual(Runs.Select(run => run.OrderKeySha256).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Pilot-A runs must be ordered by their deterministic order key.");
        }
    }

    public static ShadowRetentionPilotExecutionPlan Create(ShadowRetentionPublicationPlan publicationPlan)
    {
        ArgumentNullException.ThrowIfNull(publicationPlan);
        publicationPlan.Validate();
        var publicationHash = publicationPlan.ComputeCanonicalSha256();
        var families = publicationPlan.Families.ToDictionary(family => family.FamilyId, StringComparer.Ordinal);
        var raw = new List<RawRun>();

        foreach (var family in publicationPlan.Families)
        {
            foreach (var branchCountOrDepth in family.BranchCountsOrDepths)
            {
                foreach (var shadowFraction in family.ShadowFractions)
                {
                    foreach (var tombstoneFraction in family.TombstoneFractions)
                    {
                        var caseId = FormattableString.Invariant(
                            $"sweep-{family.FamilyId}-n{branchCountOrDepth:D3}-s{shadowFraction:R}-t{tombstoneFraction:R}");
                        raw.Add(new RawRun(
                            ShadowRetentionPilotTier.SensitivitySweep,
                            caseId,
                            family.FamilyId,
                            family.TopologyKind,
                            branchCountOrDepth,
                            shadowFraction,
                            tombstoneFraction,
                            publicationPlan.PilotSweepKeyCount,
                            publicationPlan.PilotSweepSeed,
                            ProcessRepetition: 0));
                    }
                }
            }
        }

        foreach (var selectedCase in publicationPlan.PilotRepeatedCases)
        {
            var family = families[selectedCase.FamilyId];
            foreach (var seed in publicationPlan.PilotSeeds)
            {
                for (var repetition = 0; repetition < publicationPlan.ProcessRepetitions; repetition++)
                {
                    raw.Add(new RawRun(
                        ShadowRetentionPilotTier.RepeatedSentinel,
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

        var ordered = raw
            .Select(item => (Run: item, OrderKey: ComputeOrderKey(publicationHash, item)))
            .OrderBy(item => item.OrderKey, StringComparer.Ordinal)
            .ToArray();
        var runs = new ShadowRetentionPilotRunSpec[ordered.Length];
        for (var trialOrder = 0; trialOrder < ordered.Length; trialOrder++)
        {
            var item = ordered[trialOrder];
            var runId = FormattableString.Invariant(
                $"{item.Run.Tier}:{item.Run.CaseId}:seed={item.Run.Seed}:rep={item.Run.ProcessRepetition}");
            runs[trialOrder] = new ShadowRetentionPilotRunSpec
            {
                RunId = runId,
                TrialOrder = trialOrder,
                OrderKeySha256 = item.OrderKey,
                Tier = item.Run.Tier,
                CaseId = item.Run.CaseId,
                FamilyId = item.Run.FamilyId,
                TopologyKind = item.Run.TopologyKind,
                BranchCountOrDepth = item.Run.BranchCountOrDepth,
                ShadowFraction = item.Run.ShadowFraction,
                TombstoneFraction = item.Run.TombstoneFraction,
                KeyCount = item.Run.KeyCount,
                Seed = item.Run.Seed,
                ProcessRepetition = item.Run.ProcessRepetition,
            };
        }

        var result = new ShadowRetentionPilotExecutionPlan
        {
            FormatVersion = CurrentFormatVersion,
            CandidateId = publicationPlan.CandidateId,
            PublicationPlanSha256 = publicationHash,
            Runs = Array.AsReadOnly(runs),
        };
        result.Validate();
        return result;
    }

    private static string ComputeOrderKey(string publicationHash, RawRun run)
    {
        var identity = string.Join(
            '|',
            publicationHash,
            run.Tier,
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
        => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record RawRun(
        ShadowRetentionPilotTier Tier,
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

public enum ShadowRetentionPilotTier : byte
{
    SensitivitySweep = 1,
    RepeatedSentinel = 2,
}

public sealed record ShadowRetentionPilotRunSpec
{
    public required string RunId { get; init; }

    public required int TrialOrder { get; init; }

    public required string OrderKeySha256 { get; init; }

    public required ShadowRetentionPilotTier Tier { get; init; }

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
                throw new InvalidOperationException("Pilot-A run identity fields must not be empty.");
            }
        }

        if (OrderKeySha256.Length != 64 || OrderKeySha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException("Pilot-A run order key must be a SHA-256 hex digest.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(TrialOrder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BranchCountOrDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(KeyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Seed);
        ArgumentOutOfRangeException.ThrowIfNegative(ProcessRepetition);
        if (!double.IsFinite(ShadowFraction) || ShadowFraction is < 0d or > 1d
            || !double.IsFinite(TombstoneFraction) || TombstoneFraction is < 0d or > 1d)
        {
            throw new InvalidOperationException("Pilot-A shadow/tombstone fractions must be finite [0,1].");
        }
    }
}

public sealed class ShadowRetentionPilotExecutionPlanWriter
{
    public const string PlanFileName = "a1-shadow-pilot-a-execution-plan.json";
    public const string PlanHashFileName = "a1-shadow-pilot-a-execution-plan.sha256";

    public static ShadowRetentionPilotExecutionPlanArtifact Write(
        string directoryPath,
        ShadowRetentionPilotExecutionPlan plan)
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
        return new ShadowRetentionPilotExecutionPlanArtifact(planPath, hashPath, hash);
    }

    private static void WriteImmutable(string path, string content)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path, Encoding.UTF8);
            if (!string.Equals(existing, content, StringComparison.Ordinal))
            {
                throw new IOException($"Pilot-A execution-plan artifact already exists with different content: {path}");
            }

            return;
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}

public sealed record ShadowRetentionPilotExecutionPlanArtifact(
    string PlanPath,
    string HashPath,
    string Sha256);
