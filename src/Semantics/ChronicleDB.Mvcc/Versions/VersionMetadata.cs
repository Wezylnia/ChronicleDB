using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Mvcc.Versions;

/// <summary>
/// Minimal logical metadata required by the baseline visibility rule.
/// </summary>
public readonly record struct VersionMetadata
{
    private VersionMetadata(
        VersionState state,
        CommitSequence commitSequence,
        bool isTombstone)
    {
        State = state;
        CommitSequence = commitSequence;
        IsTombstone = isTombstone;
    }

    public VersionState State { get; }

    public CommitSequence CommitSequence { get; }

    public bool IsTombstone { get; }

    public static VersionMetadata Pending(bool isTombstone = false)
        => new(VersionState.Pending, CommitSequence.Initial, isTombstone);

    public static VersionMetadata Committed(
        CommitSequence commitSequence,
        bool isTombstone = false)
    {
        if (commitSequence.IsInitial)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commitSequence),
                "A committed version must have a non-zero commit sequence.");
        }

        return new VersionMetadata(VersionState.Committed, commitSequence, isTombstone);
    }

    public static VersionMetadata Aborted(bool isTombstone = false)
        => new(VersionState.Aborted, CommitSequence.Initial, isTombstone);
}
