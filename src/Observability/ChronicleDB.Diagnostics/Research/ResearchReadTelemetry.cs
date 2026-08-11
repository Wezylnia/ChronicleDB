using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Diagnostics.Research;

public enum ResearchReadResolutionKind : byte
{
    Unknown = 0,
    LocalValue = 1,
    LocalTombstone = 2,
    InheritedValue = 3,
    InheritedTombstone = 4,
    Missing = 5,
}

public static class ResearchReadTelemetry
{
    public const string Resource = "history-read";
}

/// <summary>
/// Property-relevant ancestry information for one branch read. This metadata is
/// observational only and never participates in visibility or correctness decisions.
/// </summary>
public readonly record struct ResearchReadObservation
{
    public ResearchReadObservation(
        ResearchReadResolutionKind resolutionKind,
        int ancestorProbeCount,
        int? resolvedAncestorDepth,
        HistoryId? resolvedHistoryId)
    {
        if (resolutionKind == ResearchReadResolutionKind.Unknown)
        {
            throw new ArgumentException("Read resolution kind must be known.", nameof(resolutionKind));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ancestorProbeCount);
        if (resolvedAncestorDepth is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolvedAncestorDepth));
        }

        var isLocal = resolutionKind is ResearchReadResolutionKind.LocalValue
            or ResearchReadResolutionKind.LocalTombstone;
        var isInherited = resolutionKind is ResearchReadResolutionKind.InheritedValue
            or ResearchReadResolutionKind.InheritedTombstone;
        if (isLocal && (ancestorProbeCount != 0 || resolvedAncestorDepth != 0))
        {
            throw new ArgumentException("Local read resolutions must have zero ancestry cost.");
        }

        if (isInherited && (ancestorProbeCount <= 0 || resolvedAncestorDepth is null or <= 0))
        {
            throw new ArgumentException("Inherited read resolutions require positive ancestry cost.");
        }

        if (resolutionKind == ResearchReadResolutionKind.Missing && resolvedAncestorDepth is not null)
        {
            throw new ArgumentException("Missing reads cannot report a resolved ancestor depth.");
        }

        if (resolutionKind == ResearchReadResolutionKind.Missing && resolvedHistoryId is not null)
        {
            throw new ArgumentException("Missing reads cannot report a resolved history.");
        }

        if (resolutionKind != ResearchReadResolutionKind.Missing
            && resolvedHistoryId is not { IsValid: true })
        {
            throw new ArgumentException("Resolved reads require a valid resolved history ID.");
        }

        ResolutionKind = resolutionKind;
        AncestorProbeCount = ancestorProbeCount;
        ResolvedAncestorDepth = resolvedAncestorDepth;
        ResolvedHistoryId = resolvedHistoryId;
    }

    public ResearchReadResolutionKind ResolutionKind { get; }

    public int AncestorProbeCount { get; }

    public int? ResolvedAncestorDepth { get; }

    public HistoryId? ResolvedHistoryId { get; }

    public bool LocalMiss => ResolutionKind is ResearchReadResolutionKind.InheritedValue
        or ResearchReadResolutionKind.InheritedTombstone
        or ResearchReadResolutionKind.Missing;

    public bool TombstoneShadow => ResolutionKind is ResearchReadResolutionKind.LocalTombstone
        or ResearchReadResolutionKind.InheritedTombstone;
}

public sealed record AncestryReadMetricSnapshot(
    long ReadCount,
    long LocalReadCount,
    long InheritedReadCount,
    long MissingReadCount,
    long LocalMissCount,
    long TombstoneShadowCount,
    long AncestorProbeCount,
    int MaximumResolvedAncestorDepth,
    IReadOnlyList<long> ResolvedAncestorDepthHistogram)
{
    public int PercentileResolvedAncestorDepth(double percentile)
    {
        if (!double.IsFinite(percentile) || percentile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var observations = ResolvedAncestorDepthHistogram.Sum();
        if (observations == 0)
        {
            return 0;
        }

        var target = Math.Max(1L, checked((long)Math.Ceiling(observations * percentile)));
        long cumulative = 0;
        for (var depth = 0; depth < ResolvedAncestorDepthHistogram.Count; depth++)
        {
            cumulative = checked(cumulative + ResolvedAncestorDepthHistogram[depth]);
            if (cumulative >= target)
            {
                return depth;
            }
        }

        return ResolvedAncestorDepthHistogram.Count - 1;
    }
}

/// <summary>
/// Low-overhead ancestry metrics for P3. Non-read events are ignored.
/// </summary>
public sealed class AncestryMetricsResearchEventSink : IResearchEventSink
{
    private const int HistogramBuckets = 129;
    private readonly long[] _depthHistogram = new long[HistogramBuckets];
    private long _reads;
    private long _localReads;
    private long _inheritedReads;
    private long _missingReads;
    private long _localMisses;
    private long _tombstoneShadows;
    private long _ancestorProbes;
    private int _maximumDepth;

    public ResearchTelemetryMode Mode => ResearchTelemetryMode.Metrics;

    public void Publish(ResearchEvent researchEvent)
    {
        ArgumentNullException.ThrowIfNull(researchEvent);
        if (researchEvent.EventKind != ResearchEventKind.HistoryReadObserved)
        {
            return;
        }

        var read = researchEvent.ReadObservation
            ?? throw new InvalidOperationException("History-read telemetry is missing read metadata.");

        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _ancestorProbes, read.AncestorProbeCount);
        switch (read.ResolutionKind)
        {
            case ResearchReadResolutionKind.LocalValue:
            case ResearchReadResolutionKind.LocalTombstone:
                Interlocked.Increment(ref _localReads);
                break;
            case ResearchReadResolutionKind.InheritedValue:
            case ResearchReadResolutionKind.InheritedTombstone:
                Interlocked.Increment(ref _inheritedReads);
                Interlocked.Increment(ref _localMisses);
                break;
            case ResearchReadResolutionKind.Missing:
                Interlocked.Increment(ref _missingReads);
                Interlocked.Increment(ref _localMisses);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(researchEvent), "Unknown read resolution.");
        }

        if (read.TombstoneShadow)
        {
            Interlocked.Increment(ref _tombstoneShadows);
        }

        if (read.ResolvedAncestorDepth is { } depth)
        {
            var bucket = Math.Min(depth, HistogramBuckets - 1);
            Interlocked.Increment(ref _depthHistogram[bucket]);
            UpdateMaximumDepth(depth);
        }
    }

    public AncestryReadMetricSnapshot Snapshot()
    {
        var histogram = new long[_depthHistogram.Length];
        for (var index = 0; index < histogram.Length; index++)
        {
            histogram[index] = Volatile.Read(ref _depthHistogram[index]);
        }

        return new AncestryReadMetricSnapshot(
            Volatile.Read(ref _reads),
            Volatile.Read(ref _localReads),
            Volatile.Read(ref _inheritedReads),
            Volatile.Read(ref _missingReads),
            Volatile.Read(ref _localMisses),
            Volatile.Read(ref _tombstoneShadows),
            Volatile.Read(ref _ancestorProbes),
            Volatile.Read(ref _maximumDepth),
            Array.AsReadOnly(histogram));
    }

    private void UpdateMaximumDepth(int depth)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _maximumDepth);
            if (depth <= observed || Interlocked.CompareExchange(ref _maximumDepth, depth, observed) == observed)
            {
                return;
            }
        }
    }
}
