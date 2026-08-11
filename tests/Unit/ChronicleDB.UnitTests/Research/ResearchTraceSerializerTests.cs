using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchTraceSerializerTests
{
    [Fact]
    public void CanonicalTraceSerializationIsDeterministic()
    {
        var events = CreateEvents();

        var first = ResearchTraceSerializer.SerializeCanonical(events);
        var second = ResearchTraceSerializer.SerializeCanonical(events);

        Assert.Equal(first, second);
        Assert.Equal(
            ResearchTraceSerializer.ComputeCanonicalSha256(events),
            ResearchTraceSerializer.ComputeCanonicalSha256(events));
        Assert.Contains("\"traceFormatVersion\":1", first, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalTraceRejectsNonMonotonicEventIds()
    {
        var events = CreateEvents();

        Assert.Throws<ArgumentException>(
            () => ResearchTraceSerializer.SerializeCanonical([events[1], events[0]]));
    }

    [Fact]
    public void TraceHashChangesWhenPropertyRelevantEventChanges()
    {
        var first = CreateEvents();
        var second = new[]
        {
            first[0],
            CreateEvent(2, ResearchEventKind.CorruptionDetected),
        };

        Assert.NotEqual(
            ResearchTraceSerializer.ComputeCanonicalSha256(first),
            ResearchTraceSerializer.ComputeCanonicalSha256(second));
    }

    private static ResearchEvent[] CreateEvents()
        =>
        [
            CreateEvent(1, ResearchEventKind.RecoveryStarted),
            CreateEvent(2, ResearchEventKind.HistoryReady),
        ];

    private static ResearchEvent CreateEvent(long id, ResearchEventKind kind)
        => new(
            id,
            id,
            kind,
            new HistoryId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            parentHistoryId: null,
            Guid.Parse("00000000-0000-0000-0000-000000000010"),
            transactionId: null,
            ["main"],
            ResearchDurabilityPhase.None,
            authorityGeneration: 0,
            dependencyEventIds: id == 1 ? [] : [1],
            logicalKeyId: null,
            versionId: null,
            offset: null,
            bytes: null);
}
