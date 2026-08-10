namespace ChronicleDB.Core.Sequences;

/// <summary>
/// Identifies a logical position in committed database history.
/// </summary>
public readonly record struct CommitSequence(ulong Value) : IComparable<CommitSequence>
{
    public static CommitSequence Initial => new(0);

    public bool IsInitial => Value == 0;

    public CommitSequence Next() => new(checked(Value + 1));

    public int CompareTo(CommitSequence other) => Value.CompareTo(other.Value);

    public static bool operator <(CommitSequence left, CommitSequence right) => left.Value < right.Value;

    public static bool operator <=(CommitSequence left, CommitSequence right) => left.Value <= right.Value;

    public static bool operator >(CommitSequence left, CommitSequence right) => left.Value > right.Value;

    public static bool operator >=(CommitSequence left, CommitSequence right) => left.Value >= right.Value;
}
