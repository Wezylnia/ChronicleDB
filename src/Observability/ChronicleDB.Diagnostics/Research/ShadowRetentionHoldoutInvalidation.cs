using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public enum ShadowRetentionHoldoutInvalidationCategory : byte
{
    CorrectnessFailure = 1,
    InfrastructureFailure = 2,
}

public sealed record ShadowRetentionHoldoutInvalidation
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string CandidateId { get; init; }

    public required string RegistrationSha256 { get; init; }

    public required string HoldoutExecutionPlanSha256 { get; init; }

    public required ShadowRetentionHoldoutPartition InvalidatedPartition { get; init; }

    public required ShadowRetentionHoldoutInvalidationCategory Category { get; init; }

    public required string FailedRunId { get; init; }

    public required string FailureEvidenceSha256 { get; init; }

    public required string Reason { get; init; }

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
        if (FormatVersion != CurrentFormatVersion
            || string.IsNullOrWhiteSpace(CandidateId)
            || !IsSha256(RegistrationSha256)
            || !IsSha256(HoldoutExecutionPlanSha256)
            || InvalidatedPartition != ShadowRetentionHoldoutPartition.HoldoutA
            || !Enum.IsDefined(Category)
            || string.IsNullOrWhiteSpace(FailedRunId)
            || !IsSha256(FailureEvidenceSha256)
            || string.IsNullOrWhiteSpace(Reason))
        {
            throw new InvalidOperationException("A1 Holdout-A invalidation artifact is invalid.");
        }
    }

    public void ValidateAgainst(ShadowRetentionHoldoutRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Validate();
        registration.Validate();
        if (!string.Equals(CandidateId, registration.CandidateId, StringComparison.Ordinal)
            || !string.Equals(RegistrationSha256, registration.ComputeCanonicalSha256(), StringComparison.Ordinal)
            || !string.Equals(HoldoutExecutionPlanSha256, registration.HoldoutExecutionPlanSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout invalidation does not belong to the sealed registration.");
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

public static class ShadowRetentionHoldoutInvalidationWriter
{
    public const string FileName = "a1-shadow-holdout-a-invalidation.json";
    public const string HashFileName = "a1-shadow-holdout-a-invalidation.sha256";

    public static ShadowRetentionHoldoutInvalidationArtifact Write(
        string directoryPath,
        ShadowRetentionHoldoutInvalidation invalidation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(invalidation);
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        var json = invalidation.SerializeCanonical();
        var hash = invalidation.ComputeCanonicalSha256();
        var path = Path.Combine(directory, FileName);
        var hashPath = Path.Combine(directory, HashFileName);
        WriteImmutable(path, json + Environment.NewLine);
        WriteImmutable(hashPath, hash + Environment.NewLine);
        return new ShadowRetentionHoldoutInvalidationArtifact(path, hashPath, hash);
    }

    private static void WriteImmutable(string path, string content)
    {
        if (File.Exists(path))
        {
            if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
            {
                throw new IOException($"Holdout invalidation artifact already exists with different content: {path}");
            }
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

public sealed record ShadowRetentionHoldoutInvalidationArtifact(string Path, string HashPath, string Sha256);
