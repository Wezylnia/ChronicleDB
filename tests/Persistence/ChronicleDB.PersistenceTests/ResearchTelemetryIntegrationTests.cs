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
            events.Select(researchEvent => researchEvent.EventKind));
        Assert.Equal([1L, 2L, 3L], events.Select(researchEvent => researchEvent.LogicalEventId));
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

    private sealed class ThrowingResearchEventSink : IResearchEventSink
    {
        public ResearchTelemetryMode Mode => ResearchTelemetryMode.Trace;

        public void Publish(ResearchEvent researchEvent)
            => throw new InvalidOperationException("Test sink failure.");
    }
}
