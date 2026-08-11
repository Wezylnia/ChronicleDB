using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Immutable identity and configuration envelope for one research experiment.
/// The canonical JSON and hash are artifact metadata, never engine authority.
/// </summary>
public sealed record ExperimentManifest
{
    public const int CurrentFormatVersion = 2;
    public required Guid ExperimentId { get; init; }

    public required int ManifestFormatVersion { get; init; }

    public required int ResearchTraceFormatVersion { get; init; }

    public required string ChronicleVersion { get; init; }

    public required string GitCommit { get; init; }

    public required string BuildConfiguration { get; init; }

    public required string MachineId { get; init; }

    public required string Cpu { get; init; }

    public required long MemoryBytes { get; init; }

    public required string Disk { get; init; }

    public required string FileSystem { get; init; }

    public required string OperatingSystem { get; init; }

    public required string DotNetVersion { get; init; }

    public required int PageSize { get; init; }

    public required int KeySize { get; init; }

    public required int ValueSize { get; init; }

    public required int WorkloadSeed { get; init; }

    public required int CrashPlanSeed { get; init; }

    public required int MutationSeed { get; init; }

    public required int ProcessRepetition { get; init; }

    public required string MachineBlock { get; init; }

    public required int TrialOrder { get; init; }

    public required string WorkloadFamily { get; init; }

    public required long DurationMilliseconds { get; init; }

    public required string CacheState { get; init; }

    public required int BranchCount { get; init; }

    public required int BranchDepth { get; init; }

    public required int Fanout { get; init; }

    public required long BranchAgeMilliseconds { get; init; }

    public required double Divergence { get; init; }

    public required int SnapshotCount { get; init; }

    public required long SnapshotAgeMilliseconds { get; init; }

    public required string GcMode { get; init; }

    public required string CompactionMode { get; init; }

    public required string DurabilityMode { get; init; }

    public required string CandidateMode { get; init; }

    public required string CandidateConfigHash { get; init; }

    public required string NoveltyCardVersion { get; init; }

    public required string FailureModelVersion { get; init; }

    public required ResearchTelemetryMode TelemetryMode { get; init; }

    public required DateTimeOffset UtcStartedAt { get; init; }

    public string SerializeCanonical()
    {
        Validate();
        return JsonSerializer.Serialize(this, CanonicalJsonOptions);
    }

    public string ComputeCanonicalSha256()
    {
        var bytes = Encoding.UTF8.GetBytes(SerializeCanonical());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public void Validate()
    {
        if (ExperimentId == Guid.Empty)
        {
            throw new InvalidOperationException("ExperimentId must be non-empty.");
        }

        if (ManifestFormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported manifest format version {ManifestFormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (ResearchTraceFormatVersion <= 0)
        {
            throw new InvalidOperationException("ResearchTraceFormatVersion must be positive.");
        }

        foreach (var (name, value) in RequiredStrings())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} must not be empty.");
            }
        }

        if (MemoryBytes <= 0 || PageSize <= 0 || KeySize <= 0 || ValueSize <= 0)
        {
            throw new InvalidOperationException("Memory, page, key and value sizes must be positive.");
        }

        if (DurationMilliseconds < 0
            || BranchCount < 0
            || BranchDepth < 0
            || Fanout < 0
            || BranchAgeMilliseconds < 0
            || SnapshotCount < 0
            || SnapshotAgeMilliseconds < 0
            || ProcessRepetition < 0
            || TrialOrder < 0)
        {
            throw new InvalidOperationException("Experiment counts and durations cannot be negative.");
        }

        if (!double.IsFinite(Divergence) || Divergence is < 0 or > 1)
        {
            throw new InvalidOperationException("Divergence must be a finite value between zero and one.");
        }

        if (UtcStartedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("UtcStartedAt must use the UTC offset.");
        }
    }

    private IEnumerable<(string Name, string Value)> RequiredStrings()
    {
        yield return (nameof(ChronicleVersion), ChronicleVersion);
        yield return (nameof(GitCommit), GitCommit);
        yield return (nameof(BuildConfiguration), BuildConfiguration);
        yield return (nameof(MachineId), MachineId);
        yield return (nameof(Cpu), Cpu);
        yield return (nameof(Disk), Disk);
        yield return (nameof(FileSystem), FileSystem);
        yield return (nameof(OperatingSystem), OperatingSystem);
        yield return (nameof(DotNetVersion), DotNetVersion);
        yield return (nameof(MachineBlock), MachineBlock);
        yield return (nameof(WorkloadFamily), WorkloadFamily);
        yield return (nameof(CacheState), CacheState);
        yield return (nameof(GcMode), GcMode);
        yield return (nameof(CompactionMode), CompactionMode);
        yield return (nameof(DurabilityMode), DurabilityMode);
        yield return (nameof(CandidateMode), CandidateMode);
        yield return (nameof(CandidateConfigHash), CandidateConfigHash);
        yield return (nameof(NoveltyCardVersion), NoveltyCardVersion);
        yield return (nameof(FailureModelVersion), FailureModelVersion);
    }

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
