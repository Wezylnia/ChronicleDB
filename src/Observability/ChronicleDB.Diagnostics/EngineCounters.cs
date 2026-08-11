using System.Diagnostics;

namespace ChronicleDB.Diagnostics;

/// <summary>
/// Low-overhead process-local counters for foreground engine activity. Persistent
/// truth never depends on these counters; they are observational only.
/// </summary>
public sealed class EngineCounters
{
    private long _activeTransactions;
    private long _commitAttempts;
    private long _successfulCommits;
    private long _aborts;
    private long _conflictAborts;
    private long _commitSerializationContention;
    private long _commitSamples;
    private long _commitElapsedTicks;
    private long _snapshotCreates;
    private long _snapshotCreateSamples;
    private long _snapshotCreateElapsedTicks;
    private long _recoveryReplayedTransactions;
    private long _garbageCollectionPasses;
    private long _garbageCollectionReclaimedVersions;
    private long _garbageCollectionCheckpointBytes;
    private long _garbageCollectionElapsedTicks;
    private long _compactionPasses;
    private long _compactionBytesRewritten;
    private long _compactionBytesReclaimed;
    private long _compactionElapsedTicks;

    public void TransactionStarted() => Interlocked.Increment(ref _activeTransactions);

    public void TransactionFinished() => Interlocked.Decrement(ref _activeTransactions);

    public void CommitAttempted() => Interlocked.Increment(ref _commitAttempts);

    public void CommitSucceeded(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _successfulCommits);
        Interlocked.Increment(ref _commitSamples);
        Interlocked.Add(ref _commitElapsedTicks, elapsedStopwatchTicks);
    }

    public void AbortRecorded() => Interlocked.Increment(ref _aborts);

    public void ConflictAbortRecorded()
    {
        Interlocked.Increment(ref _conflictAborts);
        Interlocked.Increment(ref _aborts);
    }

    public void CommitSerializationContended() => Interlocked.Increment(ref _commitSerializationContention);

    public void SnapshotCreated(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _snapshotCreates);
        Interlocked.Increment(ref _snapshotCreateSamples);
        Interlocked.Add(ref _snapshotCreateElapsedTicks, elapsedStopwatchTicks);
    }

    public void RecoveryReplayed(int committedTransactions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(committedTransactions);
        Interlocked.Add(ref _recoveryReplayedTransactions, committedTransactions);
    }

    public void GarbageCollectionCompleted(
        int reclaimedVersions,
        long checkpointBytes,
        long elapsedStopwatchTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reclaimedVersions);
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointBytes);
        Interlocked.Increment(ref _garbageCollectionPasses);
        Interlocked.Add(ref _garbageCollectionReclaimedVersions, reclaimedVersions);
        Interlocked.Add(ref _garbageCollectionCheckpointBytes, checkpointBytes);
        Interlocked.Add(ref _garbageCollectionElapsedTicks, elapsedStopwatchTicks);
    }

    public void CompactionCompleted(
        long bytesRewritten,
        long bytesReclaimed,
        long elapsedStopwatchTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesRewritten);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesReclaimed);
        Interlocked.Increment(ref _compactionPasses);
        Interlocked.Add(ref _compactionBytesRewritten, bytesRewritten);
        Interlocked.Add(ref _compactionBytesReclaimed, bytesReclaimed);
        Interlocked.Add(ref _compactionElapsedTicks, elapsedStopwatchTicks);
    }

    public EngineCounterSnapshot Snapshot()
    {
        var commitSamples = Volatile.Read(ref _commitSamples);
        var snapshotSamples = Volatile.Read(ref _snapshotCreateSamples);
        return new EngineCounterSnapshot(
            ActiveTransactions: Volatile.Read(ref _activeTransactions),
            CommitAttempts: Volatile.Read(ref _commitAttempts),
            SuccessfulCommits: Volatile.Read(ref _successfulCommits),
            Aborts: Volatile.Read(ref _aborts),
            ConflictAborts: Volatile.Read(ref _conflictAborts),
            CommitSerializationContention: Volatile.Read(ref _commitSerializationContention),
            AverageCommitMilliseconds: AverageMilliseconds(Volatile.Read(ref _commitElapsedTicks), commitSamples),
            SnapshotCreates: Volatile.Read(ref _snapshotCreates),
            AverageSnapshotCreateMilliseconds: AverageMilliseconds(
                Volatile.Read(ref _snapshotCreateElapsedTicks),
                snapshotSamples),
            RecoveryReplayedTransactions: Volatile.Read(ref _recoveryReplayedTransactions),
            GarbageCollectionPasses: Volatile.Read(ref _garbageCollectionPasses),
            GarbageCollectionReclaimedVersions: Volatile.Read(ref _garbageCollectionReclaimedVersions),
            GarbageCollectionCheckpointBytes: Volatile.Read(ref _garbageCollectionCheckpointBytes),
            GarbageCollectionMilliseconds: ElapsedMilliseconds(Volatile.Read(ref _garbageCollectionElapsedTicks)),
            CompactionPasses: Volatile.Read(ref _compactionPasses),
            CompactionBytesRewritten: Volatile.Read(ref _compactionBytesRewritten),
            CompactionBytesReclaimed: Volatile.Read(ref _compactionBytesReclaimed),
            CompactionMilliseconds: ElapsedMilliseconds(Volatile.Read(ref _compactionElapsedTicks)));
    }

    private static double ElapsedMilliseconds(long elapsedTicks)
        => elapsedTicks * 1000d / Stopwatch.Frequency;

    private static double AverageMilliseconds(long elapsedTicks, long samples)
        => samples == 0
            ? 0
            : elapsedTicks * 1000d / Stopwatch.Frequency / samples;
}

public readonly record struct EngineCounterSnapshot(
    long ActiveTransactions,
    long CommitAttempts,
    long SuccessfulCommits,
    long Aborts,
    long ConflictAborts,
    long CommitSerializationContention,
    double AverageCommitMilliseconds,
    long SnapshotCreates,
    double AverageSnapshotCreateMilliseconds,
    long RecoveryReplayedTransactions,
    long GarbageCollectionPasses,
    long GarbageCollectionReclaimedVersions,
    long GarbageCollectionCheckpointBytes,
    double GarbageCollectionMilliseconds,
    long CompactionPasses,
    long CompactionBytesRewritten,
    long CompactionBytesReclaimed,
    double CompactionMilliseconds);
