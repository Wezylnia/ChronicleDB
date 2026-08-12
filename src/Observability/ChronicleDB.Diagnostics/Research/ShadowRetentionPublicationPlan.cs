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
    public const int CurrentFormatVersion = 3;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string ClaimVersion { get; init; }

    public required string LiteratureAnchorVersion { get; init; }

    public required IReadOnlyList<ShadowRetentionLiteratureAnchor> SourceAnchors { get; init; }

    public required int ValueBytes { get; init; }

    public required IReadOnlyList<int> ProjectionKeyCounts { get; init; }

    public required int PhysicalKeyCount { get; init; }

    public required int ProcessRepetitions { get; init; }

    public required IReadOnlyList<int> PilotSeeds { get; init; }

    public required IReadOnlyList<int> HoldoutASeeds { get; init; }

    public required IReadOnlyList<int> HoldoutBSeeds { get; init; }

    public required IReadOnlyList<ShadowRetentionPublicationFamily> Families { get; init; }

    public required int PilotSweepKeyCount { get; init; }

    public required int PilotSweepSeed { get; init; }

    public required IReadOnlyList<ShadowRetentionPublicationCase> PilotRepeatedCases { get; init; }

    public required IReadOnlyList<ShadowRetentionPublicationCase> HoldoutCases { get; init; }

    public required IReadOnlyList<ShadowRetentionPublicationCase> PhysicalCases { get; init; }

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

        if (SourceAnchors.Count == 0
            || SourceAnchors.Select(anchor => anchor.AnchorId).Distinct(StringComparer.Ordinal).Count() != SourceAnchors.Count)
        {
            throw new InvalidOperationException("Publication source anchors must be non-empty and uniquely named.");
        }

        var sortedAnchorIds = SourceAnchors.Select(anchor => anchor.AnchorId).Order(StringComparer.Ordinal).ToArray();
        if (!SourceAnchors.Select(anchor => anchor.AnchorId).SequenceEqual(sortedAnchorIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Publication source anchors must be sorted by AnchorId for canonical ordering.");
        }

        foreach (var anchor in SourceAnchors)
        {
            anchor.Validate();
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

        if (PilotSweepKeyCount <= 0 || !ProjectionKeyCounts.Contains(PilotSweepKeyCount))
        {
            throw new InvalidOperationException("PilotSweepKeyCount must be one of ProjectionKeyCounts.");
        }

        if (PilotSweepSeed <= 0 || !PilotSeeds.Contains(PilotSweepSeed))
        {
            throw new InvalidOperationException("PilotSweepSeed must be one of PilotSeeds.");
        }

        ValidateCases(PilotRepeatedCases, nameof(PilotRepeatedCases), requirePhysicalKeyCount: false);
        ValidateCases(HoldoutCases, nameof(HoldoutCases), requirePhysicalKeyCount: false);
        ValidateCases(PhysicalCases, nameof(PhysicalCases), requirePhysicalKeyCount: true);

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
            LiteratureAnchorVersion = "a1-source-audit-2026-08-12-v2",
            SourceAnchors =
            [
                new ShadowRetentionLiteratureAnchor
                {
                    AnchorId = "branchbench-2026",
                    Source = "BranchBench (arXiv:2604.17180)",
                    ObservedEvidence = "Published branch-workload characterization includes wide/flat failure-reproduction, simulation and data-curation patterns plus deep/narrow iterative refinement patterns.",
                    CampaignRole = "Topology-family anchor.",
                    MappingConstraint = "Does not establish a universal per-key shadow or tombstone distribution; mutation fractions remain preregistered sensitivity axes.",
                },
                new ShadowRetentionLiteratureAnchor
                {
                    AnchorId = "decibel-2016",
                    Source = "Decibel: The Relational Dataset Branching System (PVLDB 2016)",
                    ObservedEvidence = "Versioning benchmark uses 20% updates / 80% inserts by default, commits every 10,000 insert/update operations per branch, evaluates 10/50-branch settings, and reports a separate 50%-update stress workload over 10 branches.",
                    CampaignRole = "Moderate/high mutation sensitivity anchor.",
                    MappingConstraint = "Operation update share is not equivalent to the fraction of distinct inherited keys shadowed; the campaign samples nearby shadow fractions rather than relabeling Decibel's update ratio as shadow coverage.",
                },
                new ShadowRetentionLiteratureAnchor
                {
                    AnchorId = "matrixone-2026",
                    Source = "Version Control System for Data with MatrixOne (arXiv:2604.03927)",
                    ObservedEvidence = "TPC-H 100GB lineitem has about 600 million rows; change sets update 1,000, 10,000, 100,000 and 1,000,000 random rows, and a collaborative experiment uses four clones.",
                    CampaignRole = "Very-low-divergence negative-control anchor.",
                    MappingConstraint = "The MatrixOne row-update fractions are much smaller than most A1 sensitivity points and are not claimed to reproduce ChronicleDB branch lifetimes; they require retaining an explicit near-zero-benefit control region.",
                },
                new ShadowRetentionLiteratureAnchor
                {
                    AnchorId = "orpheusdb-2017",
                    Source = "ORPHEUSDB: Bolt-on Versioning for Relational Databases (PVLDB 2017)",
                    ObservedEvidence = "Reuses Decibel SCI/CUR versioning workloads; published configurations scale to 100 or 1,000 branches and up to 10,000/11,000 versions, with 1,000-8,000 insert-or-update changes from parent versions in the listed datasets.",
                    CampaignRole = "Large version-graph and branch-count anchor.",
                    MappingConstraint = "Insert-or-update count does not identify distinct-key shadow coverage; it motivates topology/scale coverage, not a direct A1 effect-size claim.",
                },
            ],
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
                    TombstoneFractions = [0.00],
                    IsNegativeControlFamily = false,
                    Interpretation = "ChronicleDB caps the source-inspired deep topology at its legal depth 16; shadow fractions are sensitivity parameters, not measured MCTS distributions.",
                },
                new ShadowRetentionPublicationFamily
                {
                    FamilyId = "low-shadow-negative-control",
                    LiteratureBasis = "MatrixOne evaluates 1K-1M random-row updates over ~600M lineitem rows; A1 retains a practical near-zero shadow region plus the existing 5/10% frontier controls rather than hiding low-divergence behavior.",
                    TopologyKind = "staggered-wide",
                    BranchCountsOrDepths = [1, 4, 8],
                    ShadowFractions = [0.001, 0.01, 0.05, 0.10],
                    TombstoneFractions = [0.00],
                    IsNegativeControlFamily = true,
                    Interpretation = "Must be retained even when it weakens aggregate effect size; it prevents universal-benefit framing.",
                },
                new ShadowRetentionPublicationFamily
                {
                    FamilyId = "published-wide-mutation-sensitivity",
                    LiteratureBasis = "Decibel supplies 20%-update default and 50%-update stress operation mixes at 10/50-branch scale; OrpheusDB reuses SCI/CUR and scales version graphs to 100/1000 branches; BranchBench supplies wide/flat topology motivation.",
                    TopologyKind = "staggered-wide",
                    BranchCountsOrDepths = [4, 8, 10, 16, 32, 50],
                    ShadowFractions = [0.05, 0.10, 0.20, 0.25, 0.50, 0.75, 1.00],
                    TombstoneFractions = [0.00, 0.25, 1.00],
                    IsNegativeControlFamily = false,
                    Interpretation = "Topology is source-anchored; shadow/tombstone fractions are preregistered sensitivity axes and must not be described as measured production distributions.",
                },
            ],
            PilotSweepKeyCount = 4096,
            PilotSweepSeed = 301,
            PilotRepeatedCases =
            [
                Case("pilot-deep-d08-s025", "branchbench-deep-refinement-sensitivity", 8, 0.25, 0.00, 4096),
                Case("pilot-deep-d16-s050", "branchbench-deep-refinement-sensitivity", 16, 0.50, 0.00, 4096),
                Case("pilot-neg-b08-s001", "low-shadow-negative-control", 8, 0.001, 0.00, 4096),
                Case("pilot-neg-b08-s010", "low-shadow-negative-control", 8, 0.10, 0.00, 4096),
                Case("pilot-wide-b08-s020", "published-wide-mutation-sensitivity", 8, 0.20, 0.00, 4096),
                Case("pilot-wide-b08-s050", "published-wide-mutation-sensitivity", 8, 0.50, 0.00, 4096),
                Case("pilot-wide-b08-s050-t100", "published-wide-mutation-sensitivity", 8, 0.50, 1.00, 4096),
                Case("pilot-wide-b16-s050-t025", "published-wide-mutation-sensitivity", 16, 0.50, 0.25, 4096),
                Case("pilot-wide-b32-s075-t025", "published-wide-mutation-sensitivity", 32, 0.75, 0.25, 4096),
            ],
            HoldoutCases =
            [
                Case("holdout-deep-d08-s025", "branchbench-deep-refinement-sensitivity", 8, 0.25, 0.00, 16384),
                Case("holdout-deep-d16-s050", "branchbench-deep-refinement-sensitivity", 16, 0.50, 0.00, 16384),
                Case("holdout-neg-b08-s001", "low-shadow-negative-control", 8, 0.001, 0.00, 16384),
                Case("holdout-wide-b08-s020", "published-wide-mutation-sensitivity", 8, 0.20, 0.00, 16384),
                Case("holdout-wide-b08-s050", "published-wide-mutation-sensitivity", 8, 0.50, 0.00, 16384),
                Case("holdout-wide-b08-s050-t100", "published-wide-mutation-sensitivity", 8, 0.50, 1.00, 16384),
                Case("holdout-wide-b16-s075-t025", "published-wide-mutation-sensitivity", 16, 0.75, 0.25, 16384),
            ],
            PhysicalCases =
            [
                Case("physical-neg-b08-s010", "low-shadow-negative-control", 8, 0.10, 0.00, 1024),
                Case("physical-wide-b08-s025", "published-wide-mutation-sensitivity", 8, 0.25, 0.00, 1024),
                Case("physical-wide-b08-s050", "published-wide-mutation-sensitivity", 8, 0.50, 0.00, 1024),
                Case("physical-wide-b08-s100", "published-wide-mutation-sensitivity", 8, 1.00, 0.00, 1024),
                Case("physical-wide-b08-s100-t100", "published-wide-mutation-sensitivity", 8, 1.00, 1.00, 1024),
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

    private void ValidateCases(
        IReadOnlyList<ShadowRetentionPublicationCase> cases,
        string name,
        bool requirePhysicalKeyCount)
    {
        if (cases.Count == 0
            || cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count() != cases.Count)
        {
            throw new InvalidOperationException($"{name} must be non-empty and uniquely named.");
        }

        var orderedIds = cases.Select(item => item.CaseId).Order(StringComparer.Ordinal).ToArray();
        if (!cases.Select(item => item.CaseId).SequenceEqual(orderedIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{name} must be sorted by CaseId for canonical ordering.");
        }

        var families = Families.ToDictionary(family => family.FamilyId, StringComparer.Ordinal);
        foreach (var item in cases)
        {
            item.Validate();
            if (!families.TryGetValue(item.FamilyId, out var family))
            {
                throw new InvalidOperationException($"Case '{item.CaseId}' references unknown family '{item.FamilyId}'.");
            }

            if (!family.BranchCountsOrDepths.Contains(item.BranchCountOrDepth)
                || !family.ShadowFractions.Contains(item.ShadowFraction)
                || !family.TombstoneFractions.Contains(item.TombstoneFraction))
            {
                throw new InvalidOperationException($"Case '{item.CaseId}' is outside its preregistered family grid.");
            }

            if (requirePhysicalKeyCount)
            {
                if (item.KeyCount != PhysicalKeyCount || !family.TopologyKind.Equals("staggered-wide", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Physical case '{item.CaseId}' must use PhysicalKeyCount and staggered-wide topology.");
                }
            }
            else if (!ProjectionKeyCounts.Contains(item.KeyCount))
            {
                throw new InvalidOperationException($"Case '{item.CaseId}' key count is outside ProjectionKeyCounts.");
            }
        }
    }

    private static ShadowRetentionPublicationCase Case(
        string caseId,
        string familyId,
        int branchCountOrDepth,
        double shadowFraction,
        double tombstoneFraction,
        int keyCount)
        => new()
        {
            CaseId = caseId,
            FamilyId = familyId,
            BranchCountOrDepth = branchCountOrDepth,
            ShadowFraction = shadowFraction,
            TombstoneFraction = tombstoneFraction,
            KeyCount = keyCount,
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

public sealed record ShadowRetentionLiteratureAnchor
{
    public required string AnchorId { get; init; }

    public required string Source { get; init; }

    public required string ObservedEvidence { get; init; }

    public required string CampaignRole { get; init; }

    public required string MappingConstraint { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { AnchorId, Source, ObservedEvidence, CampaignRole, MappingConstraint })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Publication source-anchor strings must not be empty.");
            }
        }
    }
}

public sealed record ShadowRetentionPublicationCase
{
    public required string CaseId { get; init; }

    public required string FamilyId { get; init; }

    public required int BranchCountOrDepth { get; init; }

    public required double ShadowFraction { get; init; }

    public required double TombstoneFraction { get; init; }

    public required int KeyCount { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(CaseId) || string.IsNullOrWhiteSpace(FamilyId))
        {
            throw new InvalidOperationException("Publication-case IDs must not be empty.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BranchCountOrDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(KeyCount);
        if (!double.IsFinite(ShadowFraction) || ShadowFraction is < 0d or > 1d
            || !double.IsFinite(TombstoneFraction) || TombstoneFraction is < 0d or > 1d)
        {
            throw new InvalidOperationException("Publication-case fractions must be finite values in [0,1].");
        }
    }
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
