using ChronicleDB.Core.Identifiers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
            logicalStateDigest ?? ObservationEnvelopeFingerprint.Compute(envelope),
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

public static class ObservationEnvelopeFingerprint
{
    public static string Compute(ObservationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var builder = new StringBuilder();
        Append(builder, "logical");
        if (envelope.LogicalData is { } logical)
        {
            Append(builder, logical.HistoryId.Value.ToString("N"));
            Append(builder, logical.Boundary.Value.ToString(CultureInfo.InvariantCulture));
            foreach (var entry in logical.Entries)
            {
                Append(builder, Convert.ToHexString(entry.Key.Span));
                Append(builder, entry.IsTombstone ? "tombstone" : "value");
                Append(builder, Convert.ToHexString(entry.Value.Span));
            }
        }

        Append(builder, "topology");
        foreach (var history in envelope.HistoryTopology)
        {
            Append(builder, history.HistoryId.Value.ToString("N"));
            Append(builder, history.ParentHistoryId?.Value.ToString("N"));
            Append(builder, history.BaseBoundary?.Value.ToString(CultureInfo.InvariantCulture));
            Append(builder, ((byte)history.Lifecycle).ToString(CultureInfo.InvariantCulture));
        }

        Append(builder, "roots");
        foreach (var root in envelope.RootLifecycle)
        {
            Append(builder, root.RootId.Value.ToString("N"));
            Append(builder, ((byte)root.Kind).ToString(CultureInfo.InvariantCulture));
            Append(builder, root.OwnerHistoryId.Value.ToString("N"));
            Append(builder, root.ProtectedHistoryId.Value.ToString("N"));
            Append(builder, root.Boundary.Value.ToString(CultureInfo.InvariantCulture));
            Append(builder, ((byte)root.Lifecycle).ToString(CultureInfo.InvariantCulture));
        }

        Append(builder, "authority");
        Append(builder, envelope.Authority.WalGeneration.ToString(CultureInfo.InvariantCulture));
        Append(builder, envelope.Authority.CheckpointGeneration.ToString(CultureInfo.InvariantCulture));
        Append(builder, envelope.Authority.PublishedAuthority);

        Append(builder, "sequences");
        foreach (var sequence in envelope.Sequences)
        {
            Append(builder, sequence.HistoryId.Value.ToString("N"));
            Append(builder, sequence.CommittedSequence.Value.ToString(CultureInfo.InvariantCulture));
            Append(builder, sequence.RetentionFloor.Value.ToString(CultureInfo.InvariantCulture));
        }

        Append(builder, ((byte)envelope.Availability.State).ToString(CultureInfo.InvariantCulture));
        Append(builder, ((byte)envelope.Error.Kind).ToString(CultureInfo.InvariantCulture));
        Append(builder, envelope.Error.Code);
        Append(builder, envelope.Corruption.Detected ? "corrupt" : "clean");
        Append(builder, envelope.Corruption.Code);
        Append(builder, envelope.SafetyPredicates.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= "<null>";
        builder.Append(value.Length).Append(':').Append(value).Append('|');
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
