using System.Diagnostics;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Trace sink that records monotonic publication timestamps for performance-only
/// research measurements. Logical event IDs remain the ordering authority; elapsed
/// time is never consumed by correctness or recovery decisions.
/// </summary>
public sealed class TimedTraceResearchEventSink : IResearchEventSink, IResearchEventSequence
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly List<TimedResearchEvent> _events = [];
    private long _lastLogicalEventId;

    public ResearchTelemetryMode Mode => ResearchTelemetryMode.Trace;

    public long LastLogicalEventId => Interlocked.Read(ref _lastLogicalEventId);

    public void Publish(ResearchEvent researchEvent)
    {
        ArgumentNullException.ThrowIfNull(researchEvent);
        var timestamp = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _events.Add(new TimedResearchEvent(researchEvent, Stopwatch.GetElapsedTime(_started, timestamp)));
            Interlocked.Exchange(ref _lastLogicalEventId, researchEvent.LogicalEventId);
        }
    }

    public IReadOnlyList<TimedResearchEvent> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_events.ToArray());
        }
    }
}

public sealed record TimedResearchEvent(ResearchEvent Event, TimeSpan Elapsed);
