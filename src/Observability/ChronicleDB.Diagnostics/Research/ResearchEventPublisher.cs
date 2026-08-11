namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Serializes trace publication and isolates engine behavior from telemetry failures.
/// Disabled mode exits before taking the publication lock.
/// </summary>
public sealed class ResearchEventPublisher
{
    private readonly IResearchEventSink _sink;
    private readonly object _gate = new();
    private long _nextLogicalEventId;
    private long _publicationFailures;
    private bool _faulted;

    public ResearchEventPublisher(IResearchEventSink? sink = null)
    {
        _sink = sink ?? NullResearchEventSink.Instance;
        _nextLogicalEventId = _sink is IResearchEventSequence sequence
            ? sequence.LastLogicalEventId
            : 0;
    }

    public ResearchTelemetryMode Mode => _sink.Mode;

    public bool IsFaulted
    {
        get
        {
            lock (_gate)
            {
                return _faulted;
            }
        }
    }

    public long PublicationFailures => Interlocked.Read(ref _publicationFailures);

    public ResearchTelemetryStatus SnapshotStatus()
    {
        lock (_gate)
        {
            return new ResearchTelemetryStatus(
                _sink.Mode,
                _faulted,
                Interlocked.Read(ref _publicationFailures),
                _nextLogicalEventId);
        }
    }

    public bool TryPublish(Func<long, ResearchEvent> eventFactory, out long logicalEventId)
    {
        ArgumentNullException.ThrowIfNull(eventFactory);
        logicalEventId = 0;

        if (_sink.Mode == ResearchTelemetryMode.Disabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (_faulted)
            {
                return false;
            }

            logicalEventId = checked(++_nextLogicalEventId);
            try
            {
                var researchEvent = eventFactory(logicalEventId)
                    ?? throw new InvalidOperationException("The research event factory returned null.");
                _sink.Publish(researchEvent);
                return true;
            }
            catch
            {
                _faulted = true;
                Interlocked.Increment(ref _publicationFailures);
                return false;
            }
        }
    }
}
