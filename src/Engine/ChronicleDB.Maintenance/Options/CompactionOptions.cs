namespace ChronicleDB.Maintenance;

/// <summary>
/// Basic v0.9 foreground-compaction throttle. Compaction is deliberately bounded
/// rather than rewriting every history on each call.
/// </summary>
public sealed class CompactionOptions
{
    public int MaxHistoriesPerPass { get; init; } = 4;

    public long MinimumReclaimableBytes { get; init; } = 1;

    public long MaxBytesRewrittenPerPass { get; init; } = long.MaxValue;

    public void Validate()
    {
        if (MaxHistoriesPerPass <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxHistoriesPerPass),
                "Compaction must allow at least one history per pass.");
        }
        if (MinimumReclaimableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumReclaimableBytes),
                "Minimum reclaimable bytes cannot be negative.");
        }
        if (MaxBytesRewrittenPerPass <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBytesRewrittenPerPass),
                "Compaction rewrite budget must be positive.");
        }
    }
}
