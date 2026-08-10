namespace ChronicleDB.Core.Identifiers;

/// <summary>
/// One-based physical page identity. Zero is reserved as the invalid/sentinel value.
/// </summary>
public readonly record struct PageId(ulong Value)
{
    public static PageId Invalid => new(0);

    public bool IsValid => Value != 0;
}
