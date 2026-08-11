using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Diagnostics.Research;

[Flags]
public enum SafetyPredicateMask : byte
{
    None = 0,
    NoPhantomCommit = 1 << 0,
    NoCrossHistoryReplay = 1 << 1,
    BaseStable = 1 << 2,
    NoInvalidRoot = 1 << 3,
    NoPrematureReclaim = 1 << 4,
    NoEarlyPublication = 1 << 5,
}

/// <summary>
/// Property-relevant observation point. Low-level implementation events are
/// intentionally absent; equivalent stuttering is normalized by the trace.
/// </summary>
public readonly record struct ObservationTracePoint
{
    public ObservationTracePoint(
        ResearchEventKind eventKind,
        HistoryId historyId,
        ResearchDurabilityPhase durabilityPhase,
        ObservationAvailability availability,
        ObservationErrorKind error,
        bool corruptionDetected,
        ulong authorityGeneration,
        SafetyPredicateMask safetyPredicates,
        string? logicalStateDigest,
        string? errorCode)
    {
        if (eventKind == ResearchEventKind.Unknown)
        {
            throw new ArgumentException("A canonical observation cannot have an unknown event kind.", nameof(eventKind));
        }

        if (!historyId.IsValid)
        {
            throw new ArgumentException("A canonical observation requires a valid history ID.", nameof(historyId));
        }

        EventKind = eventKind;
        HistoryId = historyId;
        DurabilityPhase = durabilityPhase;
        Availability = availability;
        Error = error;
        CorruptionDetected = corruptionDetected;
        AuthorityGeneration = authorityGeneration;
        SafetyPredicates = safetyPredicates;
        LogicalStateDigest = logicalStateDigest;
        ErrorCode = errorCode;
    }

    public ResearchEventKind EventKind { get; }

    public HistoryId HistoryId { get; }

    public ResearchDurabilityPhase DurabilityPhase { get; }

    public ObservationAvailability Availability { get; }

    public ObservationErrorKind Error { get; }

    public bool CorruptionDetected { get; }

    public ulong AuthorityGeneration { get; }

    public SafetyPredicateMask SafetyPredicates { get; }

    public string? LogicalStateDigest { get; }

    public string? ErrorCode { get; }

    public static ObservationTracePoint FromEnvelope(
        ResearchEventKind eventKind,
        HistoryId historyId,
        ResearchDurabilityPhase durabilityPhase,
        ulong authorityGeneration,
        ObservationEnvelope envelope,
        string? logicalStateDigest = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new ObservationTracePoint(
            eventKind,
            historyId,
            durabilityPhase,
            envelope.Availability.State,
            envelope.Error.Kind,
            envelope.Corruption.Detected,
            authorityGeneration,
            ToSafetyMask(envelope.SafetyPredicates),
            logicalStateDigest,
            envelope.Error.Code);
    }

    private static SafetyPredicateMask ToSafetyMask(SafetyPredicateObservation safety)
    {
        var mask = SafetyPredicateMask.None;
        if (safety.NoPhantomCommit) mask |= SafetyPredicateMask.NoPhantomCommit;
        if (safety.NoCrossHistoryReplay) mask |= SafetyPredicateMask.NoCrossHistoryReplay;
        if (safety.BaseStable) mask |= SafetyPredicateMask.BaseStable;
        if (safety.NoInvalidRoot) mask |= SafetyPredicateMask.NoInvalidRoot;
        if (safety.NoPrematureReclaim) mask |= SafetyPredicateMask.NoPrematureReclaim;
        if (safety.NoEarlyPublication) mask |= SafetyPredicateMask.NoEarlyPublication;
        return mask;
    }
}

/// <summary>
/// Canonical property-relevant trace used by bounded POR comparison.
/// Consecutive identical observations are stuttering and collapse to one point.
/// </summary>
public sealed class CanonicalObservationTrace
{
    public CanonicalObservationTrace(IEnumerable<ObservationTracePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var canonical = new List<ObservationTracePoint>();
        foreach (var point in points)
        {
            if (canonical.Count == 0 || canonical[^1] != point)
            {
                canonical.Add(point);
            }
        }

        Points = Array.AsReadOnly(canonical.ToArray());
    }

    public IReadOnlyList<ObservationTracePoint> Points { get; }

    public bool EquivalentTo(CanonicalObservationTrace? other)
        => other is not null && Points.SequenceEqual(other.Points);
}
