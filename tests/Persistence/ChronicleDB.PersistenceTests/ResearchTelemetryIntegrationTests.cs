using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ResearchTelemetryIntegrationTests
{
    [Fact]
    public void OpenPublishesRecoveryMilestonesWithoutChangingDefaultState()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        {
            database.Put([0x01], [0x02]);
            Assert.True(database.TryGet([0x01], out var value));
            Assert.Equal([0x02], value);
        }

        var events = sink.Snapshot();

        Assert.Equal(
            [
                ResearchEventKind.RecoveryStarted,
                ResearchEventKind.HistoryReady,
                ResearchEventKind.RecoveryCompleted,
            ],
            events.Take(3).Select(researchEvent => researchEvent.EventKind));
        Assert.Equal([1L, 2L, 3L], events.Take(3).Select(researchEvent => researchEvent.LogicalEventId));
        Assert.Empty(events[0].DependencyEventIds);
        Assert.Equal([1L], events[1].DependencyEventIds);
        Assert.Equal([2L], events[2].DependencyEventIds);
        Assert.All(events, researchEvent => Assert.True(researchEvent.HistoryId.IsValid));
    }

    [Fact]
    public void TelemetrySinkFailureDoesNotAbortDatabaseOpen()
    {
        using var directory = new StorageTestDirectory();
        var sink = new ThrowingResearchEventSink();

        using var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink);

        database.Put([0x01], [0x02]);
        Assert.True(database.TryGet([0x01], out _));
    }

    [Fact]
    public void CommitPublishesDurabilityAndAuthorityMilestones()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        {
            database.Put([0x01], [0x02]);
        }

        var events = sink.Snapshot();
        var commitEvents = events.Skip(3).ToArray();

        Assert.Equal(
            [
                ResearchEventKind.OperationStarted,
                ResearchEventKind.DurabilityBarrier,
                ResearchEventKind.AuthorityPublished,
                ResearchEventKind.OperationCompleted,
            ],
            commitEvents.Select(researchEvent => researchEvent.EventKind));
        Assert.Equal([4L, 5L, 6L, 7L], commitEvents.Select(researchEvent => researchEvent.LogicalEventId));
        Assert.Equal([4L], commitEvents[1].DependencyEventIds);
        Assert.Equal([5L], commitEvents[2].DependencyEventIds);
        Assert.Equal([6L], commitEvents[3].DependencyEventIds);
        Assert.All(commitEvents, researchEvent => Assert.Equal(1UL, researchEvent.AuthorityGeneration));
    }

    [Fact]
    public void BranchCommitTraceUsesBranchHistoryAndResources()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        using (var branch = database.CreateBranch("trace-branch"))
        {
            branch.Put([0x01], [0x02]);
        }

        var branchEvents = sink
            .Snapshot()
            .Where(researchEvent => researchEvent.EventKind == ResearchEventKind.OperationStarted
                && researchEvent.ResourceSet.Any(resource => resource.StartsWith("branch-", StringComparison.Ordinal)))
            .ToArray();

        Assert.Single(branchEvents);
        var branchHistoryId = branchEvents[0].HistoryId;
        Assert.NotEqual(Guid.Empty, branchHistoryId.Value);
        Assert.Contains(
            branchEvents[0].ResourceSet,
            resource => resource.EndsWith("-data", StringComparison.Ordinal));
        Assert.Contains(
            branchEvents[0].ResourceSet,
            resource => resource.EndsWith("-wal", StringComparison.Ordinal));
    }

    private sealed class ThrowingResearchEventSink : IResearchEventSink
    {
        public ResearchTelemetryMode Mode => ResearchTelemetryMode.Trace;

        public void Publish(ResearchEvent researchEvent)
            => throw new InvalidOperationException("Test sink failure.");
    }
}
