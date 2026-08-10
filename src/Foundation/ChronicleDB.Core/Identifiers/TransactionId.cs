namespace ChronicleDB.Core.Identifiers;

/// <summary>
/// Stable identity for one logical transaction.
/// </summary>
public readonly record struct TransactionId(Guid Value)
{
    public static TransactionId Empty => new(Guid.Empty);

    public bool IsValid => Value != Guid.Empty;

    public static TransactionId New() => new(Guid.NewGuid());
}
