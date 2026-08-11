using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchExperimentSessionTests
{
    [Fact]
    public void SessionWritesManifestAndWorkloadAtConstruction()
    {
        using var directory = new TemporaryDirectory();
        var manifest = CreateManifest();
        var workload = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 3, 4);

        var session = new ResearchExperimentSession(
            new ResearchArtifactWriter(directory.Path),
            manifest,
            workload);

        Assert.False(session.TraceCompleted);
        Assert.True(File.Exists(session.ManifestArtifact.ManifestPath));
        Assert.True(File.Exists(session.WorkloadArtifact.WorkloadPath));
        Assert.Equal(workload, session.Workload);
    }

    [Fact]
    public void TraceCanBeCompletedOnlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var session = new ResearchExperimentSession(
            new ResearchArtifactWriter(directory.Path),
            CreateManifest(),
            DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 3, 2));

        var first = session.Complete([CreateEvent(1)]);

        Assert.True(session.TraceCompleted);
        Assert.Equal(
            first.Sha256,
            File.ReadAllText(Path.Combine(directory.Path, ResearchArtifactWriter.TraceHashFileName)));
        Assert.Throws<InvalidOperationException>(() => session.Complete([CreateEvent(1)]));
    }

    private static ResearchEvent CreateEvent(long id)
        => new(
            id,
            id,
            ResearchEventKind.OperationCompleted,
            new HistoryId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            null,
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
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
            WorkloadFamily = "S0",
            DurationMilliseconds = 1000,
            BranchCount = 0,
            BranchDepth = 0,
            Fanout = 0,
            BranchAgeMilliseconds = 0,
            Divergence = 0,
            SnapshotCount = 0,
            SnapshotAgeMilliseconds = 0,
            GcMode = "disabled",
            CompactionMode = "disabled",
            DurabilityMode = "durable",
            CandidateMode = "baseline",
            CandidateConfigHash = "candidate-a",
            NoveltyCardVersion = "v1",
            FailureModelVersion = "persistence-v1",
            TelemetryMode = ResearchTelemetryMode.Trace,
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
