namespace ChronicleDB.Core.Keys;

/// <summary>
/// An immutable, engine-owned binary key.
/// </summary>
public sealed class BinaryKey : IEquatable<BinaryKey>
{
    private readonly byte[] _bytes;

    public BinaryKey(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();
    }

    public int Length => _bytes.Length;

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public bool Equals(BinaryKey? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is BinaryKey other && Equals(other);

    public override int GetHashCode()
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;

        foreach (var value in _bytes)
        {
            hash ^= value;
            hash *= prime;
        }

        return unchecked((int)hash);
    }

    public static bool operator ==(BinaryKey? left, BinaryKey? right) => Equals(left, right);

    public static bool operator !=(BinaryKey? left, BinaryKey? right) => !Equals(left, right);
}
