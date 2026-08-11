using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Diagnostics.Research;

public enum ResearchTelemetryMode : byte
{
    Disabled = 0,
    Metrics = 1,
    Trace = 2,
}

public enum ResearchEventKind : byte
{
    Unknown = 0,
    OperationStarted = 1,
    OperationCompleted = 2,
    DurabilityBarrier = 3,
    AuthorityAccepted = 4,
    AuthorityPublished = 5,
    HistoryValidated = 6,
    HistoryReady = 7,
    RecoveryStarted = 8,
    RecoveryCompleted = 9,
    CorruptionDetected = 10,
    RootTransition = 11,
    SafetyPredicateEvaluated = 12,
    HistoryReadObserved = 13,
}

public enum ResearchDurabilityPhase : byte
{
    None = 0,
    Prepared = 1,
    WalAppended = 2,
    StableStorageBarrier = 3,
    AuthorityPublished = 4,
    Cleanup = 5,
}

/// <summary>
/// Immutable event for metrics and property-relevant research traces.
/// Logical event IDs, not wall-clock timestamps, define ordering.
/// </summary>
public sealed class ResearchEvent
{
    public ResearchEvent(
        long logicalEventId,
        long logicalClock,
        ResearchEventKind eventKind,
        HistoryId historyId,
        HistoryId? parentHistoryId,
        Guid operationId,
        Guid? transactionId,
        IEnumerable<string> resourceSet,
        ResearchDurabilityPhase durabilityPhase,
        ulong authorityGeneration,
        IEnumerable<long> dependencyEventIds,
        string? logicalKeyId,
        ulong? versionId,
        long? offset,
        long? bytes,
        ResearchReadObservation? readObservation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalEventId);
        ArgumentOutOfRangeException.ThrowIfNegative(logicalClock);
        if (!historyId.IsValid)
        {
            throw new ArgumentException("A research event requires a valid history ID.", nameof(historyId));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A research event requires an operation ID.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(resourceSet);
        ArgumentNullException.ThrowIfNull(dependencyEventIds);
        if (resourceSet.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Resource IDs cannot be empty.", nameof(resourceSet));
        }

        if (dependencyEventIds.Any(id => id <= 0 || id >= logicalEventId))
        {
            throw new ArgumentException(
                "Dependencies must reference earlier positive logical event IDs.",
                nameof(dependencyEventIds));
        }

        if (offset is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (bytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        LogicalEventId = logicalEventId;
        LogicalClock = logicalClock;
        EventKind = eventKind;
        HistoryId = historyId;
        ParentHistoryId = parentHistoryId;
        OperationId = operationId;
        TransactionId = transactionId;
        ResourceSet = Array.AsReadOnly(resourceSet.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        DurabilityPhase = durabilityPhase;
        AuthorityGeneration = authorityGeneration;
        DependencyEventIds = Array.AsReadOnly(dependencyEventIds.Distinct().Order().ToArray());
        if (readObservation is not null && eventKind != ResearchEventKind.HistoryReadObserved)
        {
            throw new ArgumentException(
                "Read observations are valid only for HistoryReadObserved events.",
                nameof(readObservation));
        }

        if (eventKind == ResearchEventKind.HistoryReadObserved && readObservation is null)
        {
            throw new ArgumentException(
                "HistoryReadObserved events require read observation metadata.",
                nameof(readObservation));
        }

        LogicalKeyId = logicalKeyId;
        VersionId = versionId;
        Offset = offset;
        Bytes = bytes;
        ReadObservation = readObservation;
    }

    public long LogicalEventId { get; }

    public long LogicalClock { get; }

    public ResearchEventKind EventKind { get; }

    public HistoryId HistoryId { get; }

    public HistoryId? ParentHistoryId { get; }

    public Guid OperationId { get; }

    public Guid? TransactionId { get; }

    public IReadOnlyList<string> ResourceSet { get; }

    public ResearchDurabilityPhase DurabilityPhase { get; }

    public ulong AuthorityGeneration { get; }

    public IReadOnlyList<long> DependencyEventIds { get; }

    public string? LogicalKeyId { get; }

    public ulong? VersionId { get; }

    public long? Offset { get; }

    public long? Bytes { get; }

    public ResearchReadObservation? ReadObservation { get; }
}

public interface IResearchEventSink
{
    ResearchTelemetryMode Mode { get; }

    void Publish(ResearchEvent researchEvent);
}

public interface IResearchEventSequence
{
    long LastLogicalEventId { get; }
}

/// <summary>
/// Point-in-time health of the observational publication seam. Research runs
/// must reject a faulted/incomplete telemetry stream, while the engine itself
/// continues to preserve normal semantics when telemetry fails.
/// </summary>
public sealed record ResearchTelemetryStatus(
    ResearchTelemetryMode Mode,
    bool IsFaulted,
    long PublicationFailures,
    long LastLogicalEventId)
{
    public bool IsComplete => !IsFaulted && PublicationFailures == 0;
}

public sealed class NullResearchEventSink : IResearchEventSink
{
    public static NullResearchEventSink Instance { get; } = new();

    private NullResearchEventSink()
    {
    }

    public ResearchTelemetryMode Mode => ResearchTelemetryMode.Disabled;

    public void Publish(ResearchEvent researchEvent)
    {
        ArgumentNullException.ThrowIfNull(researchEvent);
    }
}

public sealed class MetricsResearchEventSink : IResearchEventSink
{
    private readonly long[] _eventCounts = new long[Enum.GetValues<ResearchEventKind>().Length];
    private long _publishedEvents;

    public ResearchTelemetryMode Mode => ResearchTelemetryMode.Metrics;

    public void Publish(ResearchEvent researchEvent)
    {
        ArgumentNullException.ThrowIfNull(researchEvent);
        var kind = (int)researchEvent.EventKind;
        if ((uint)kind >= (uint)_eventCounts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(researchEvent), "Unknown event kind.");
        }

        Interlocked.Increment(ref _publishedEvents);
        Interlocked.Increment(ref _eventCounts[kind]);
    }

    public ResearchMetricSnapshot Snapshot()
    {
        var counts = new long[_eventCounts.Length];
        for (var index = 0; index < counts.Length; index++)
        {
            counts[index] = Volatile.Read(ref _eventCounts[index]);
        }

        return new ResearchMetricSnapshot(Volatile.Read(ref _publishedEvents), counts);
    }
}

public sealed class ResearchMetricSnapshot
{
    public ResearchMetricSnapshot(long publishedEvents, IReadOnlyList<long> eventCounts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(publishedEvents);
        ArgumentNullException.ThrowIfNull(eventCounts);
        if (eventCounts.Any(count => count < 0))
        {
            throw new ArgumentException("Event counts cannot be negative.", nameof(eventCounts));
        }

        PublishedEvents = publishedEvents;
        EventCounts = Array.AsReadOnly(eventCounts.ToArray());
    }

    public long PublishedEvents { get; }

    public IReadOnlyList<long> EventCounts { get; }
}

public sealed class TraceResearchEventSink : IResearchEventSink, IResearchEventSequence
{
    private readonly object _gate = new();
    private readonly List<ResearchEvent> _events = [];
    private long _lastLogicalEventId;

    public ResearchTelemetryMode Mode => ResearchTelemetryMode.Trace;

    public long LastLogicalEventId
    {
        get
        {
            lock (_gate)
            {
                return _lastLogicalEventId;
            }
        }
    }

    public void Publish(ResearchEvent researchEvent)
    {
        ArgumentNullException.ThrowIfNull(researchEvent);
        lock (_gate)
        {
            if (researchEvent.LogicalEventId <= _lastLogicalEventId)
            {
                throw new InvalidOperationException(
                    "Research trace logical event IDs must be strictly increasing.");
            }

            _events.Add(researchEvent);
            _lastLogicalEventId = researchEvent.LogicalEventId;
        }
    }

    public IReadOnlyList<ResearchEvent> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_events.ToArray());
        }
    }
}
