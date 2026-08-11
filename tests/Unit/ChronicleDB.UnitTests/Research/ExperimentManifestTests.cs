using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ExperimentManifestTests
{
    [Fact]
    public void CanonicalSerializationAndHashAreStable()
    {
        var first = CreateManifest();
        var second = CreateManifest();

        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
    }

    [Fact]
    public void CandidateConfigurationChangesCanonicalHash()
    {
        var first = CreateManifest();
        var second = first with { CandidateConfigHash = "candidate-b" };

        Assert.NotEqual(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
    }

    [Fact]
    public void InvalidManifestIsRejectedBeforeSerialization()
    {
        var invalid = CreateManifest() with { Divergence = 1.1 };

        Assert.Throws<InvalidOperationException>(() => invalid.SerializeCanonical());
    }

    [Fact]
    public void NonUtcTimestampIsRejected()
    {
        var invalid = CreateManifest() with
        {
            UtcStartedAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.FromHours(3)),
        };

        Assert.Throws<InvalidOperationException>(() => invalid.Validate());
    }

    [Fact]
    public void CanonicalSerializationContainsRequiredResearchIdentity()
    {
        var manifest = CreateManifest();
        var serialized = manifest.SerializeCanonical();

        Assert.Contains("\"manifestFormatVersion\":1", serialized, StringComparison.Ordinal);
        Assert.Contains("\"candidateMode\":\"Paper1Baseline\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"telemetryMode\":1", serialized, StringComparison.Ordinal);
    }

    private static ExperimentManifest CreateManifest()
        => new()
        {
            ExperimentId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ManifestFormatVersion = 1,
            ResearchTraceFormatVersion = 1,
            ChronicleVersion = "v1.1-research",
            GitCommit = "0123456789abcdef",
            BuildConfiguration = "Release",
            MachineId = "machine-test",
            Cpu = "test-cpu",
            MemoryBytes = 16L * 1024 * 1024 * 1024,
            Disk = "test-disk",
            FileSystem = "NTFS",
            OperatingSystem = "Windows",
            DotNetVersion = "10.0.0",
            PageSize = 4096,
            KeySize = 32,
            ValueSize = 1024,
            WorkloadSeed = 1,
            CrashPlanSeed = 2,
            MutationSeed = 3,
            ProcessRepetition = 1,
            MachineBlock = "block-1",
            TrialOrder = 1,
            WorkloadFamily = "S1",
            DurationMilliseconds = 1000,
            BranchCount = 4,
            BranchDepth = 2,
            Fanout = 2,
            BranchAgeMilliseconds = 10_000,
            Divergence = 0.25,
            SnapshotCount = 2,
            SnapshotAgeMilliseconds = 5_000,
            GcMode = "enabled",
            CompactionMode = "copy-publish",
            DurabilityMode = "durable",
            CandidateMode = "Paper1Baseline",
            CandidateConfigHash = "candidate-a",
            NoveltyCardVersion = "a1-v1",
            FailureModelVersion = "persistence-v1",
            TelemetryMode = ResearchTelemetryMode.Metrics,
            UtcStartedAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
        };
}
