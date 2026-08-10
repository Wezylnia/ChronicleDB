namespace ChronicleDB.Core.Identifiers;

/// <summary>
/// Stable identity of an independently evolving logical history domain.
/// A commit sequence is meaningful only together with its history ID.
/// </summary>
public readonly record struct HistoryId(Guid Value)
{
    public static HistoryId Empty => new(Guid.Empty);

    public bool IsValid => Value != Guid.Empty;

    public static HistoryId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
