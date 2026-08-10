namespace ChronicleDB.Core.Identifiers;

/// <summary>
/// Stable logical identity of a writable database branch.
/// </summary>
public readonly record struct BranchId(Guid Value)
{
    public static BranchId Empty => new(Guid.Empty);

    public bool IsValid => Value != Guid.Empty;

    public static BranchId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
