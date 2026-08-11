using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchTelemetryTests
{
    [Fact]
    public void ResearchEventCanonicalizesResourcesAndDependencies()
    {
        var researchEvent = CreateEvent(
            logicalEventId: 3,
            resources: ["wal", "catalog", "wal"],
            dependencies: [2, 1, 2]);

        Assert.Equal(["catalog", "wal"], researchEvent.ResourceSet);
        Assert.Equal([1L, 2L], researchEvent.DependencyEventIds);
        Assert.True(researchEvent.HistoryId.IsValid);
        Assert.NotEqual(Guid.Empty, researchEvent.OperationId);
    }

    [Fact]
    public void ResearchEventRejectsFutureDependencies()
    {
        Assert.Throws<ArgumentException>(
            () => CreateEvent(logicalEventId: 2, resources: ["wal"], dependencies: [2]));
    }

    [Fact]
    public void NullSinkIsDisabledAndAcceptsObservationalEvents()
    {
        var sink = NullResearchEventSink.Instance;

        sink.Publish(CreateEvent(1, ["wal"], []));

        Assert.Equal(ResearchTelemetryMode.Disabled, sink.Mode);
    }

    [Fact]
    public void MetricsSinkCountsPublishedEventsByKind()
    {
        var sink = new MetricsResearchEventSink();

        sink.Publish(CreateEvent(1, ["wal"], []));
        sink.Publish(CreateEvent(2, ["wal"], [1], ResearchEventKind.AuthorityPublished));

        var snapshot = sink.Snapshot();

        Assert.Equal(2, snapshot.PublishedEvents);
        Assert.Equal(1, snapshot.EventCounts[(int)ResearchEventKind.OperationStarted]);
        Assert.Equal(1, snapshot.EventCounts[(int)ResearchEventKind.AuthorityPublished]);
    }

    [Fact]
    public void TraceSinkRequiresStrictLogicalEventOrder()
    {
        var sink = new TraceResearchEventSink();
        sink.Publish(CreateEvent(1, ["wal"], []));

        Assert.Throws<InvalidOperationException>(
            () => sink.Publish(CreateEvent(1, ["wal"], [])));
    }

    [Fact]
    public void TraceSinkSnapshotIsDetachedFromInternalCollection()
    {
        var sink = new TraceResearchEventSink();
        sink.Publish(CreateEvent(1, ["wal"], []));

        var snapshot = sink.Snapshot();

        sink.Publish(CreateEvent(2, ["wal"], [1]));

        Assert.Single(snapshot);
        Assert.Equal(2, sink.Snapshot().Count);
    }

    private static ResearchEvent CreateEvent(
        long logicalEventId,
        IReadOnlyList<string> resources,
        IReadOnlyList<long> dependencies,
        ResearchEventKind eventKind = ResearchEventKind.OperationStarted)
    {
        return new ResearchEvent(
            logicalEventId,
            logicalEventId,
            eventKind,
            HistoryId.New(),
            parentHistoryId: null,
            Guid.NewGuid(),
            transactionId: null,
            resources,
            ResearchDurabilityPhase.Prepared,
            authorityGeneration: 1,
            dependencies,
            logicalKeyId: null,
            versionId: null,
            offset: null,
            bytes: null);
    }
}
