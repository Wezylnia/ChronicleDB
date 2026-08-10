using ChronicleDB.Core.Keys;

namespace ChronicleDB.ReferenceModel;

/// <summary>
/// Small independent logical oracle used by deterministic correctness workloads.
/// It intentionally has no persistence or transaction implementation to avoid
/// reproducing engine bugs in the oracle.
/// </summary>
public sealed class ReferenceKeyValueModel
{
    private readonly Dictionary<BinaryKey, byte[]> _values = [];

    public int Count => _values.Count;

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        => _values[new BinaryKey(key)] = value.ToArray();

    public bool Delete(ReadOnlySpan<byte> key)
        => _values.Remove(new BinaryKey(key));

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        if (_values.TryGetValue(new BinaryKey(key), out var stored))
        {
            value = stored.ToArray();
            return true;
        }

        value = [];
        return false;
    }
}
