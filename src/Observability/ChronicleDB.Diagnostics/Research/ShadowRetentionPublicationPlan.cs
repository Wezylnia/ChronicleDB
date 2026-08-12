using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Immutable preregistration envelope for the A1 shadow-aware retention publication campaign.
/// Topology choices are anchored in published branch-workload shapes; shadow/tombstone values
/// are explicitly sensitivity parameters rather than claims about production distributions.
/// </summary>
public sealed record ShadowRetentionPublicationPlan
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string ClaimVersion { get; init; }

    public required string LiteratureAnchorVersion { get; init; }

    public required int ValueBytes { get; init; }

    public required IReadOnlyList<int> ProjectionKeyCounts { get; init; }

    public required int PhysicalKeyCount { get; init; }

    public required int ProcessRepetitions { get; init; }

    public required IReadOnlyList<int> PilotSeeds { get; init; }

    public required IReadOnlyList<int> HoldoutASeeds { get; init; }

    public required IReadOnlyList<int> HoldoutBSeeds { get; init; }

    public required IReadOnlyList<ShadowRetentionPublicationFamily> Families { get; init; }

    public required IReadOnlyList<string> MandatoryCorrectnessGates { get; init; }

    public required IReadOnlyList<string> InterpretationRules { get; init; }

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
                $"Unsupported A1 publication-plan format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        foreach (var (name, value) in new[]
        {
            (nameof(CandidateId), CandidateId),
            (nameof(ClaimVersion), ClaimVersion),
            (nameof(LiteratureAnchorVersion), LiteratureAnchorVersion),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} must not be empty.");
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ValueBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PhysicalKeyCount);
        if (ProcessRepetitions is < 2 or > 20)
        {
            throw new InvalidOperationException("ProcessRepetitions must be between 2 and 20.");
        }

        ValidatePositiveUniqueSorted(ProjectionKeyCounts, nameof(ProjectionKeyCounts));
        ValidatePositiveUniqueSorted(PilotSeeds, nameof(PilotSeeds));
        ValidatePositiveUniqueSorted(HoldoutASeeds, nameof(HoldoutASeeds));
        ValidatePositiveUniqueSorted(HoldoutBSeeds, nameof(HoldoutBSeeds));

        if (PilotSeeds.Intersect(HoldoutASeeds).Any()
            || PilotSeeds.Intersect(HoldoutBSeeds).Any()
            || HoldoutASeeds.Intersect(HoldoutBSeeds).Any())
        {
            throw new InvalidOperationException("Pilot/Holdout seed partitions must be disjoint.");
        }

        if (Families.Count == 0
            || Families.Select(family => family.FamilyId).Distinct(StringComparer.Ordinal).Count() != Families.Count)
        {
            throw new InvalidOperationException("Publication families must be non-empty and uniquely named.");
        }

        var sortedFamilyIds = Families.Select(family => family.FamilyId).Order(StringComparer.Ordinal).ToArray();
        if (!Families.Select(family => family.FamilyId).SequenceEqual(sortedFamilyIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Publication families must be sorted by FamilyId for canonical ordering.");
        }

        foreach (var family in Families)
        {
            family.Validate();
        }

        if (MandatoryCorrectnessGates.Count == 0 || InterpretationRules.Count == 0)
        {
            throw new InvalidOperationException("Publication plan requires correctness gates and interpretation rules.");
        }
    }

    public static ShadowRetentionPublicationPlan CreateDefault()
        => new()
        {
            FormatVersion = CurrentFormatVersion,
            CandidateId = "A1-shadow-retention",
            ClaimVersion = "semantic-shadow-aware-mvcc-projection-v2",
            LiteratureAnchorVersion = "branchbench-arxiv-2604.17180+helios-zenodo-19242034+cow-clone-attack-2026-08-12",
            ValueBytes = 4096,
            ProjectionKeyCounts = [4096, 16384],
            PhysicalKeyCount = 1024,
            ProcessRepetitions = 3,
            PilotSeeds = [301, 302, 303, 304, 305],
            HoldoutASeeds = [1101, 1102, 1103, 1104, 1105, 1106, 1107, 1108, 1109, 1110],
            HoldoutBSeeds = [2101, 2102, 2103, 2104, 2105, 2106, 2107, 2108, 2109, 2110],
            Families =
            [
                new ShadowRetentionPublicationFamily
                {
                    FamilyId = "branchbench-deep-refinement-sensitivity",
                    LiteratureBasis = "BranchBench MCTS: deep/narrow trees; successive refinements may reach tens/hundreds of levels with 3-10 children.",
                    TopologyKind = "nested-chain",
                    BranchCountsOrDepths = [2, 4, 8, 16],
                    ShadowFractions = [0.10, 0.25, 0.50, 0.75, 1.00],
                    TombstoneFractions = [0.00, 0.25],
                    IsNegativeControlFamily = false,
                    Interpretation = "ChronicleDB caps the source-inspired deep topology at its legal depth 16; shadow fractions are sensitivity parameters, not measured MCTS distributions.",
                },
                new ShadowRetentionPublicationFamily
                {
                    FamilyId = "branchbench-wide-mutation-sensitivity",
                    LiteratureBasis = "BranchBench failure-reproduction/simulation/data-curation: wide or flat branch sets with write-intensive or bulk update/delete mutations.",
                    TopologyKind = "staggered-wide",
                    BranchCountsOrDepths = [4, 8, 16, 32],
                    ShadowFractions = [0.05, 0.10, 0.25, 0.50, 0.75, 1.00],
                    TombstoneFractions = [0.00, 0.25, 1.00],
                    IsNegativeControlFamily = false,
                    Interpretation = "Topology is source-anchored; shadow/tombstone fractions are preregistered sensitivity axes and must not be described as measured production distributions.",
                },
                new ShadowRetentionPublicationFamily
                {
                    FamilyId = "low-shadow-negative-control",
                    LiteratureBasis = "A1 benefit-frontier control; deliberately outside the expected high-benefit regime.",
                    TopologyKind = "staggered-wide",
                    BranchCountsOrDepths = [1, 8],
                    ShadowFractions = [0.01, 0.05, 0.10],
                    TombstoneFractions = [0.00],
                    IsNegativeControlFamily = true,
                    Interpretation = "Must be retained even when it weakens aggregate effect size; it prevents universal-benefit framing.",
                },
            ],
            MandatoryCorrectnessGates =
            [
                "independent-flat-exact-baseline-equality",
                "candidate-subset-of-flat-exact",
                "observer-equivalence",
                "observer-witness-minimality",
                "effect-model-equality-on-controlled-families",
                "physical-restart-observer-equivalence",
                "physical-allocation-measurement-exact",
                "descendant-first-crash-recovery-safety",
            ],
            InterpretationRules =
            [
                "Report every preregistered sensitivity point, including low-shadow negative controls.",
                "Do not call shadow/tombstone sensitivity fractions production distributions unless an external trace source measures them.",
                "Use retained-set reduction, physical realization, observer preservation, and crash-safe publication as the primary claims; maintenance runtime is secondary.",
                "Do not claim generic copy-on-write reachability, page/extent reference counting, or clone shadowing as novel; the candidate claim is MVCC observer-semantic projection plus durable authority transition.",
                "Do not retune candidate mechanics or workload axes after opening Holdout-A.",
                "Open Holdout-B only if Holdout-A is invalidated by a preregistered correctness/infrastructure failure, never because its effect size is weak.",
            ],
        };

    private static void ValidatePositiveUniqueSorted(IReadOnlyList<int> values, string name)
    {
        if (values.Count == 0 || values.Any(value => value <= 0))
        {
            throw new InvalidOperationException($"{name} must contain positive values.");
        }

        if (values.Distinct().Count() != values.Count || !values.SequenceEqual(values.Order()))
        {
            throw new InvalidOperationException($"{name} must be unique and sorted.");
        }
    }

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

public sealed record ShadowRetentionPublicationFamily
{
    public required string FamilyId { get; init; }

    public required string LiteratureBasis { get; init; }

    public required string TopologyKind { get; init; }

    public required IReadOnlyList<int> BranchCountsOrDepths { get; init; }

    public required IReadOnlyList<double> ShadowFractions { get; init; }

    public required IReadOnlyList<double> TombstoneFractions { get; init; }

    public required bool IsNegativeControlFamily { get; init; }

    public required string Interpretation { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { FamilyId, LiteratureBasis, TopologyKind, Interpretation })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Publication-family strings must not be empty.");
            }
        }

        if (BranchCountsOrDepths.Count == 0
            || BranchCountsOrDepths.Any(value => value <= 0)
            || BranchCountsOrDepths.Distinct().Count() != BranchCountsOrDepths.Count
            || !BranchCountsOrDepths.SequenceEqual(BranchCountsOrDepths.Order()))
        {
            throw new InvalidOperationException($"Family '{FamilyId}' topology values must be positive, unique and sorted.");
        }

        ValidateFractions(ShadowFractions, nameof(ShadowFractions));
        ValidateFractions(TombstoneFractions, nameof(TombstoneFractions));
    }

    private void ValidateFractions(IReadOnlyList<double> values, string name)
    {
        if (values.Count == 0
            || values.Any(value => !double.IsFinite(value) || value is < 0d or > 1d)
            || values.Distinct().Count() != values.Count
            || !values.SequenceEqual(values.Order()))
        {
            throw new InvalidOperationException(
                $"Family '{FamilyId}' {name} must be finite [0,1], unique and sorted.");
        }
    }
}

public sealed class ShadowRetentionPublicationPlanWriter
{
    public const string PlanFileName = "a1-shadow-publication-plan.json";
    public const string PlanHashFileName = "a1-shadow-publication-plan.sha256";

    public static ShadowRetentionPublicationPlanArtifact Write(string directoryPath, ShadowRetentionPublicationPlan plan)
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
        return new ShadowRetentionPublicationPlanArtifact(planPath, hashPath, hash);
    }

    private static void WriteImmutable(string path, string content)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path, Encoding.UTF8);
            if (!string.Equals(existing, content, StringComparison.Ordinal))
            {
                throw new IOException($"Publication-plan artifact already exists with different content: {path}");
            }

            return;
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}

public sealed record ShadowRetentionPublicationPlanArtifact(
    string PlanPath,
    string HashPath,
    string Sha256);
