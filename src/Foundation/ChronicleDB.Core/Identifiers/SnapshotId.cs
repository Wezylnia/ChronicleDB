namespace ChronicleDB.Core.Identifiers;

/// <summary>
/// Stable persistent identity of a named historical snapshot.
/// </summary>
public readonly record struct SnapshotId(Guid Value)
{
    public static SnapshotId Empty => new(Guid.Empty);

    public bool IsValid => Value != Guid.Empty;

    public static SnapshotId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
