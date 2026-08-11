using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchArtifactWriterTests
{
    [Fact]
    public void WritesCanonicalManifestAndMatchingHashSidecar()
    {
        using var directory = new TemporaryDirectory();
        var manifest = CreateManifest();
        var artifact = new ResearchArtifactWriter(directory.Path).WriteManifest(manifest);

        Assert.Equal(manifest.SerializeCanonical(), File.ReadAllText(artifact.ManifestPath));
        Assert.Equal(manifest.ComputeCanonicalSha256(), artifact.Sha256);
        Assert.Equal(artifact.Sha256, File.ReadAllText(artifact.ManifestHashPath));
    }

    [Fact]
    public void RewritingIdenticalManifestIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        var manifest = CreateManifest();

        var first = writer.WriteManifest(manifest);
        var second = writer.WriteManifest(manifest);

        Assert.Equal(first, second);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void RewritingManifestWithDifferentContentIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        writer.WriteManifest(CreateManifest());

        var changed = CreateManifest() with { CandidateConfigHash = "changed" };

        Assert.Throws<IOException>(() => writer.WriteManifest(changed));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void ExistingMismatchedHashSidecarIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        writer.WriteManifest(CreateManifest());
        File.WriteAllText(Path.Combine(directory.Path, ResearchArtifactWriter.ManifestHashFileName), "bad");

        Assert.Throws<IOException>(() => writer.WriteManifest(CreateManifest()));
    }

    [Fact]
    public void WritesCanonicalTraceAndMatchingHashSidecar()
    {
        using var directory = new TemporaryDirectory();
        var events = new TraceResearchEventSink();
        events.Publish(CreateEvent(1));

        var artifact = new ResearchArtifactWriter(directory.Path).WriteTrace(events.Snapshot());

        Assert.Equal(ResearchTraceSerializer.SerializeCanonical(events.Snapshot()), File.ReadAllText(artifact.TracePath));
        Assert.Equal(ResearchTraceSerializer.ComputeCanonicalSha256(events.Snapshot()), artifact.Sha256);
        Assert.Equal(artifact.Sha256, File.ReadAllText(artifact.TraceHashPath));
    }

    [Fact]
    public void RewritingTraceWithDifferentContentIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        writer.WriteTrace([CreateEvent(1)]);

        Assert.Throws<IOException>(() => writer.WriteTrace([CreateEvent(1), CreateEvent(2)]));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void WritesCanonicalWorkloadAndMatchingHashSidecar()
    {
        using var directory = new TemporaryDirectory();
        var operations = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 9, 8);

        var artifact = new ResearchArtifactWriter(directory.Path).WriteWorkload(operations);

        Assert.Equal(ResearchWorkloadSerializer.SerializeCanonical(operations), File.ReadAllText(artifact.WorkloadPath));
        Assert.Equal(ResearchWorkloadSerializer.ComputeCanonicalSha256(operations), artifact.Sha256);
        Assert.Equal(artifact.Sha256, File.ReadAllText(artifact.WorkloadHashPath));
    }

    [Fact]
    public void RewritingWorkloadWithDifferentContentIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        writer.WriteWorkload(DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 9, 8));

        Assert.Throws<IOException>(() => writer.WriteWorkload(
            DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 10, 8)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void WritesCanonicalCrashPlanAndMatchingHashSidecar()
    {
        using var directory = new TemporaryDirectory();
        var operations = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S5RecoveryHeavy, 2, 16);
        var plan = ResearchCrashPlanFactory.Create(operations, 3);

        var artifact = new ResearchArtifactWriter(directory.Path).WriteCrashPlan(plan);

        Assert.Equal(ResearchCrashPlanSerializer.SerializeCanonical(plan), File.ReadAllText(artifact.CrashPlanPath));
        Assert.Equal(ResearchCrashPlanSerializer.ComputeCanonicalSha256(plan), artifact.Sha256);
        Assert.Equal(artifact.Sha256, File.ReadAllText(artifact.CrashPlanHashPath));
    }

    private static ResearchEvent CreateEvent(long id)
        => new(
            id,
            id,
            ResearchEventKind.OperationCompleted,
            new ChronicleDB.Core.Identifiers.HistoryId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            null,
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            null,
            ["main-data"],
            ResearchDurabilityPhase.AuthorityPublished,
            1,
            id == 1 ? [] : [1],
            null,
            null,
            null,
            null);

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronicle-research-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
