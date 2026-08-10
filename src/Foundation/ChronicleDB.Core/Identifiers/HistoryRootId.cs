namespace ChronicleDB.Core.Identifiers;

/// <summary>
/// Stable identity of a retained historical root.
/// </summary>
public readonly record struct HistoryRootId(Guid Value)
{
    public static HistoryRootId Empty => new(Guid.Empty);

    public bool IsValid => Value != Guid.Empty;

    public static HistoryRootId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
