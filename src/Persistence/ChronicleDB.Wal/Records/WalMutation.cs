using ChronicleDB.Core.Keys;

namespace ChronicleDB.Wal.Records;

public sealed class WalMutation
{
    private readonly byte[] _value;

    internal WalMutation(BinaryKey key, bool isDelete, ReadOnlySpan<byte> value)
    {
        Key = key;
        IsDelete = isDelete;
        _value = value.ToArray();
    }

    public BinaryKey Key { get; }

    public bool IsDelete { get; }

    public ReadOnlyMemory<byte> Value => _value;
}
