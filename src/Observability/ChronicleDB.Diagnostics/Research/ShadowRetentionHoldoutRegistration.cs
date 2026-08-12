using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public sealed record ShadowRetentionHoldoutRegistration
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string PublicationPlanSha256 { get; init; }

    public required string HoldoutExecutionPlanSha256 { get; init; }

    public required string HoldoutAnalysisPlanSha256 { get; init; }

    public required string ExpectedMainBaseCommit { get; init; }

    public required string SourceCommit { get; init; }

    public required string SourceTree { get; init; }

    public required bool SourceTreeClean { get; init; }

    public required bool ExpectedMainBaseIsAncestor { get; init; }

    public required string MachineBlockId { get; init; }

    public required string FrameworkDescription { get; init; }

    public required string DotNetSdkVersion { get; init; }

    public required string MachineIdentitySha256 { get; init; }

    public required string OsDescription { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required string OsArchitecture { get; init; }

    public required int HoldoutARunCount { get; init; }

    public required int HoldoutBRunCount { get; init; }

    public required ShadowRetentionHoldoutPartition InitialPartition { get; init; }

    public required bool HoldoutBSealedBeforeA { get; init; }

    public required IReadOnlyList<ShadowRetentionBinaryArtifactIdentity> BinaryArtifacts { get; init; }

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
                $"Unsupported A1 holdout-registration format {FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(CandidateId)
            || !IsSha256(PublicationPlanSha256)
            || !IsSha256(HoldoutExecutionPlanSha256)
            || !IsSha256(HoldoutAnalysisPlanSha256))
        {
            throw new InvalidOperationException("Holdout registration plan identities are invalid.");
        }

        if (!IsGitObjectId(ExpectedMainBaseCommit)
            || !IsGitObjectId(SourceCommit)
            || !IsGitObjectId(SourceTree)
            || !SourceTreeClean
            || !ExpectedMainBaseIsAncestor)
        {
            throw new InvalidOperationException(
                "Holdout registration requires a clean source tree descended from the declared main base.");
        }

        if (!IsSha256(MachineIdentitySha256))
        {
            throw new InvalidOperationException("Holdout machine identity SHA-256 is invalid.");
        }

        foreach (var value in new[]
        {
            MachineBlockId,
            FrameworkDescription,
            DotNetSdkVersion,
            OsDescription,
            ProcessArchitecture,
            OsArchitecture,
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Holdout registration environment identity fields must not be empty.");
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HoldoutARunCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HoldoutBRunCount);
        if (InitialPartition != ShadowRetentionHoldoutPartition.HoldoutA || !HoldoutBSealedBeforeA)
        {
            throw new InvalidOperationException("Holdout registration must begin with A after B is sealed.");
        }

        if (BinaryArtifacts.Count == 0
            || BinaryArtifacts.Select(artifact => artifact.Name).Distinct(StringComparer.Ordinal).Count() != BinaryArtifacts.Count
            || !BinaryArtifacts.Select(artifact => artifact.Name)
                .SequenceEqual(BinaryArtifacts.Select(artifact => artifact.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Holdout binary artifacts must be non-empty, unique and ordinal-sorted.");
        }

        foreach (var artifact in BinaryArtifacts)
        {
            artifact.Validate();
        }
    }

    public void ValidateAgainst(
        ShadowRetentionPublicationPlan publicationPlan,
        ShadowRetentionHoldoutExecutionPlan executionPlan,
        ShadowRetentionHoldoutAnalysisPlan analysisPlan)
    {
        ArgumentNullException.ThrowIfNull(publicationPlan);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(analysisPlan);
        Validate();
        publicationPlan.Validate();
        executionPlan.ValidateAgainst(publicationPlan);
        analysisPlan.ValidateAgainst(publicationPlan, executionPlan);

        if (!string.Equals(CandidateId, publicationPlan.CandidateId, StringComparison.Ordinal)
            || !string.Equals(PublicationPlanSha256, publicationPlan.ComputeCanonicalSha256(), StringComparison.Ordinal)
            || !string.Equals(HoldoutExecutionPlanSha256, executionPlan.ComputeCanonicalSha256(), StringComparison.Ordinal)
            || !string.Equals(HoldoutAnalysisPlanSha256, analysisPlan.ComputeCanonicalSha256(), StringComparison.Ordinal)
            || HoldoutARunCount != executionPlan.HoldoutARunCount
            || HoldoutBRunCount != executionPlan.HoldoutBRunCount)
        {
            throw new InvalidOperationException("Holdout registration does not match the frozen A1 plans.");
        }
    }

    public ShadowRetentionHoldoutRegistration WithCurrentBinaryArtifacts(
        IReadOnlyList<ShadowRetentionBinaryArtifactIdentity> binaryArtifacts)
        => this with { BinaryArtifacts = binaryArtifacts };

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool IsGitObjectId(string value)
        => (value.Length is 40 or 64) && value.All(char.IsAsciiHexDigit);

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

public sealed record ShadowRetentionBinaryArtifactIdentity
{
    public required string Name { get; init; }

    public required long LengthBytes { get; init; }

    public required string Sha256 { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)
            || Name.Contains('/')
            || Name.Contains('\\')
            || LengthBytes <= 0
            || Sha256.Length != 64
            || Sha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException("Holdout binary artifact identity is invalid.");
        }
    }
}

public static class ShadowRetentionHoldoutRegistrationWriter
{
    public const string RegistrationFileName = "a1-shadow-holdout-registration.json";
    public const string RegistrationHashFileName = "a1-shadow-holdout-registration.sha256";

    public static ShadowRetentionHoldoutRegistrationArtifact Write(
        string directoryPath,
        ShadowRetentionHoldoutRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(registration);
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        var json = registration.SerializeCanonical();
        var hash = registration.ComputeCanonicalSha256();
        var registrationPath = Path.Combine(directory, RegistrationFileName);
        var hashPath = Path.Combine(directory, RegistrationHashFileName);
        WriteImmutable(registrationPath, json + Environment.NewLine);
        WriteImmutable(hashPath, hash + Environment.NewLine);
        return new ShadowRetentionHoldoutRegistrationArtifact(registrationPath, hashPath, hash);
    }

    private static void WriteImmutable(string path, string content)
    {
        if (File.Exists(path))
        {
            if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
            {
                throw new IOException($"Holdout registration artifact already exists with different content: {path}");
            }
            return;
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

public sealed record ShadowRetentionHoldoutRegistrationArtifact(
    string RegistrationPath,
    string RegistrationHashPath,
    string Sha256);
