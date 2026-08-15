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
    public void MetricsSinkAggregatesAncestryReadObservations()
    {
        var sink = new AncestryMetricsResearchEventSink();
        var researchEvent = new ResearchEvent(
            logicalEventId: 1,
            logicalClock: 1,
            ResearchEventKind.HistoryReadObserved,
            HistoryId.New(),
            parentHistoryId: HistoryId.New(),
            Guid.NewGuid(),
            transactionId: null,
            ["history-read"],
            ResearchDurabilityPhase.None,
            authorityGeneration: 0,
            dependencyEventIds: [],
            logicalKeyId: "key",
            versionId: null,
            offset: null,
            bytes: null,
            readObservation: new ResearchReadObservation(
                ResearchReadResolutionKind.InheritedValue,
                ancestorProbeCount: 3,
                resolvedAncestorDepth: 3,
                resolvedHistoryId: HistoryId.New()));

        sink.Publish(researchEvent);
        var snapshot = sink.Snapshot();

        Assert.Equal(1, snapshot.InheritedReadCount);
        Assert.Equal(0, snapshot.LocalReadCount);
        Assert.Equal(1, snapshot.LocalMissCount);
        Assert.Equal(3, snapshot.AncestorProbeCount);
        Assert.Equal(3, snapshot.MaximumResolvedAncestorDepth);
        Assert.Equal(3, snapshot.PercentileResolvedAncestorDepth(0.99));
    }

    [Fact]
    public void HistoryReadObservedRequiresReadObservationMetadata()
    {
        Assert.Throws<ArgumentException>(() => new ResearchEvent(
            logicalEventId: 1,
            logicalClock: 1,
            ResearchEventKind.HistoryReadObserved,
            HistoryId.New(),
            parentHistoryId: null,
            Guid.NewGuid(),
            transactionId: null,
            ["history-read"],
            ResearchDurabilityPhase.None,
            authorityGeneration: 0,
            dependencyEventIds: [],
            logicalKeyId: "key",
            versionId: null,
            offset: null,
            bytes: null));
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



    [Fact]
    public void NewPublisherContinuesLogicalIdsFromExistingTraceSink()
    {
        var sink = new TraceResearchEventSink();
        var firstPublisher = new ResearchEventPublisher(sink);
        firstPublisher.TryPublish(id => CreateEvent(id, ["wal"], []), out _);

        var secondPublisher = new ResearchEventPublisher(sink);
        var published = secondPublisher.TryPublish(
            id => CreateEvent(id, ["wal"], [1], ResearchEventKind.AuthorityPublished),
            out var eventId);

        Assert.True(published);
        Assert.Equal(2, eventId);
        Assert.Equal(2, sink.LastLogicalEventId);
    }

    [Fact]
    public void PublisherDoesNotInvokeFactoryWhenTelemetryIsDisabled()
    {
        var publisher = new ResearchEventPublisher();
        var invoked = false;

        var published = publisher.TryPublish(
            _ =>
            {
                invoked = true;
                return CreateEvent(1, ["wal"], []);
            },
            out var eventId);

        Assert.False(published);
        Assert.False(invoked);
        Assert.Equal(0, eventId);
    }

    [Fact]
    public void PublisherSerializesTraceIdsAndSuppressesSinkFailures()
    {
        var sink = new ThrowingResearchEventSink();
        var publisher = new ResearchEventPublisher(sink);

        var published = publisher.TryPublish(
            id => CreateEvent(id, ["wal"], []),
            out var eventId);

        Assert.False(published);
        Assert.Equal(1, eventId);
        Assert.True(publisher.IsFaulted);
        Assert.Equal(1, publisher.PublicationFailures);
        Assert.False(publisher.TryPublish(
            id => CreateEvent(id, ["wal"], []),
            out var suppressedId));
        Assert.Equal(0, suppressedId);
    }

    private sealed class ThrowingResearchEventSink : IResearchEventSink
    {
        public ResearchTelemetryMode Mode => ResearchTelemetryMode.Trace;

        public void Publish(ResearchEvent researchEvent)
            => throw new InvalidOperationException("Test sink failure.");
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
