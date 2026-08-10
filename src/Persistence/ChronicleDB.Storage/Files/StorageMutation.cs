using ChronicleDB.Core.Keys;

namespace ChronicleDB.Storage.Files;

/// <summary>
/// A validated, owned mutation used by the transaction publication boundary.
/// </summary>
public sealed class StorageMutation
{
    private readonly byte[] _value;

    public StorageMutation(BinaryKey key, bool isDelete, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (isDelete && !value.IsEmpty)
        {
            throw new ArgumentException("A delete mutation cannot contain a value.", nameof(value));
        }

        Key = key;
        IsDelete = isDelete;
        _value = value.ToArray();
    }

    public BinaryKey Key { get; }

    public bool IsDelete { get; }

    public ReadOnlyMemory<byte> Value => _value;
}
