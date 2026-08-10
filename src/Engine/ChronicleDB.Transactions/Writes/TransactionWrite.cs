using ChronicleDB.Core.Keys;

namespace ChronicleDB.Transactions.Writes;

/// <summary>
/// Immutable transaction-local mutation. The value is owned by this object.
/// </summary>
public sealed class TransactionWrite
{
    private readonly byte[] _value;

    internal TransactionWrite(BinaryKey key, bool isDelete, ReadOnlySpan<byte> value)
    {
        Key = key;
        IsDelete = isDelete;
        _value = value.ToArray();
    }

    public BinaryKey Key { get; }

    public bool IsDelete { get; }

    public ReadOnlyMemory<byte> Value => _value;

    internal TransactionWrite Clone()
        => new(Key, IsDelete, _value);
}
