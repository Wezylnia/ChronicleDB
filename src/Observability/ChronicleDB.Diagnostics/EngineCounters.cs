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
            RecoveryReplayedTransactions: Volatile.Read(ref _recoveryReplayedTransactions));
    }

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
    long RecoveryReplayedTransactions);
