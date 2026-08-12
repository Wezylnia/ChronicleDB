using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public enum ResearchCampaignPartition
{
    PilotA = 0,
    HoldoutA = 1,
    HoldoutB = 2,
}

/// <summary>
/// Immutable identity for one preregistered campaign run. The manifest hash binds the
/// eventual execution to a configuration generated before holdout results are opened.
/// </summary>
public sealed record ResearchCampaignRunRegistration(
    Guid ExperimentId,
    ResearchCampaignPartition Partition,
    int WorkloadSeed,
    int CrashPlanSeed,
    int MutationSeed,
    int ProcessRepetition,
    string MachineBlock,
    int TrialOrder,
    string ManifestSha256)
{
    public void Validate()
    {
        if (ExperimentId == Guid.Empty)
        {
            throw new InvalidOperationException("ExperimentId must be non-empty.");
        }

        if (ProcessRepetition < 0 || TrialOrder < 0)
        {
            throw new InvalidOperationException("Process repetition and trial order cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(MachineBlock))
        {
            throw new InvalidOperationException("MachineBlock must not be empty.");
        }

        ValidateSha256(ManifestSha256, nameof(ManifestSha256));
    }

    internal static void ValidateSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException($"{name} must be a 64-character hexadecimal SHA-256 value.");
        }
    }
}

/// <summary>
/// Preregistration envelope for Pilot-A and sealed Holdout-A/B runs. It is research
/// metadata only and never participates in engine correctness or durability decisions.
/// </summary>
public sealed record ResearchCampaignRegistration
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string CandidateConfigHash { get; init; }

    public required string NoveltyCardVersion { get; init; }

    public required string FailureModelVersion { get; init; }

    public required DateTimeOffset UtcSealedAt { get; init; }

    public required IReadOnlyList<ResearchCampaignRunRegistration> Runs { get; init; }

    public string SerializeCanonical()
    {
        Validate();
        var canonical = this with
        {
            Runs = Runs
                .OrderBy(run => run.Partition)
                .ThenBy(run => run.TrialOrder)
                .ThenBy(run => run.ExperimentId)
                .ToArray(),
        };
        return JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
    }

    public string ComputeCanonicalSha256()
    {
        var bytes = Encoding.UTF8.GetBytes(SerializeCanonical());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported campaign-registration format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        foreach (var (name, value) in new[]
        {
            (nameof(CandidateId), CandidateId),
            (nameof(CandidateConfigHash), CandidateConfigHash),
            (nameof(NoveltyCardVersion), NoveltyCardVersion),
            (nameof(FailureModelVersion), FailureModelVersion),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} must not be empty.");
            }
        }

        if (UtcSealedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("UtcSealedAt must use the UTC offset.");
        }

        if (Runs is null || Runs.Count == 0)
        {
            throw new InvalidOperationException("A campaign registration must contain at least one run.");
        }

        var experimentIds = new HashSet<Guid>();
        var identities = new HashSet<(ResearchCampaignPartition Partition, string MachineBlock, int TrialOrder)>();
        foreach (var run in Runs)
        {
            ArgumentNullException.ThrowIfNull(run);
            run.Validate();
            if (!experimentIds.Add(run.ExperimentId))
            {
                throw new InvalidOperationException($"Duplicate ExperimentId in campaign: {run.ExperimentId}.");
            }

            if (!identities.Add((run.Partition, run.MachineBlock, run.TrialOrder)))
            {
                throw new InvalidOperationException(
                    $"Duplicate trial order {run.TrialOrder} in {run.Partition}/{run.MachineBlock}.");
            }
        }

        var holdoutA = Runs.Where(run => run.Partition == ResearchCampaignPartition.HoldoutA).ToArray();
        var holdoutB = Runs.Where(run => run.Partition == ResearchCampaignPartition.HoldoutB).ToArray();
        if ((holdoutA.Length == 0) != (holdoutB.Length == 0))
        {
            throw new InvalidOperationException("Holdout-A and Holdout-B must either both be sealed or both be absent.");
        }

        if (holdoutA.Length > 0)
        {
            var aInputs = holdoutA.Select(InputIdentity).ToHashSet();
            var bInputs = holdoutB.Select(InputIdentity).ToHashSet();
            if (aInputs.Overlaps(bInputs))
            {
                throw new InvalidOperationException("Holdout-A and Holdout-B must not reuse the same registered input identity.");
            }
        }
    }

    private static (int WorkloadSeed, int CrashPlanSeed, int MutationSeed, int ProcessRepetition)
        InputIdentity(ResearchCampaignRunRegistration run)
        => (run.WorkloadSeed, run.CrashPlanSeed, run.MutationSeed, run.ProcessRepetition);

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

public enum ResearchCandidateDisposition
{
    Supported = 0,
    Weakened = 1,
    Falsified = 2,
    Inconclusive = 3,
    BlockedByNovelty = 4,
    BlockedBySemantics = 5,
}

/// <summary>
/// Immutable recorded disposition for one candidate at a research gate. It records
/// evidence, not an automatic paper-selection score.
/// </summary>
public sealed record ResearchCandidateGateDecision
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required ResearchCandidateDisposition Disposition { get; init; }

    public required string NarrowClaimVersion { get; init; }

    public required string Rationale { get; init; }

    public required DateTimeOffset UtcRecordedAt { get; init; }

    public required IReadOnlyList<string> EvidenceSha256 { get; init; }

    public string SerializeCanonical()
    {
        Validate();
        var canonical = this with
        {
            EvidenceSha256 = EvidenceSha256
                .Select(hash => hash.ToLowerInvariant())
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };
        return JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
    }

    public string ComputeCanonicalSha256()
    {
        var bytes = Encoding.UTF8.GetBytes(SerializeCanonical());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported candidate-gate format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(CandidateId)
            || string.IsNullOrWhiteSpace(NarrowClaimVersion)
            || string.IsNullOrWhiteSpace(Rationale))
        {
            throw new InvalidOperationException("CandidateId, NarrowClaimVersion and Rationale must not be empty.");
        }

        if (UtcRecordedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("UtcRecordedAt must use the UTC offset.");
        }

        if (EvidenceSha256 is null || EvidenceSha256.Count == 0)
        {
            throw new InvalidOperationException("A candidate disposition must cite at least one evidence artifact hash.");
        }

        foreach (var hash in EvidenceSha256)
        {
            ResearchCampaignRunRegistration.ValidateSha256(hash, nameof(EvidenceSha256));
        }
    }

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
