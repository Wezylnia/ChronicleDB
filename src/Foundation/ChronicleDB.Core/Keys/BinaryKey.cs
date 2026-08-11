namespace ChronicleDB.Core.Keys;

/// <summary>
/// Immutable, engine-owned binary key with structural equality.
/// </summary>
public sealed class BinaryKey : IEquatable<BinaryKey>
{
    private readonly byte[] _bytes;
    private readonly int _hashCode;

    public BinaryKey(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();

        // BinaryKey instances are immutable, so hashing once avoids rescanning the key on
        // every dictionary/index lookup. System.HashCode is process-seeded on modern .NET,
        // which also avoids exposing a fixed public hash function to adversarial key sets.
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        _hashCode = hash.ToHashCode();
    }

    public int Length => _bytes.Length;

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public bool Equals(BinaryKey? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is BinaryKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public static bool operator ==(BinaryKey? left, BinaryKey? right) => Equals(left, right);

    public static bool operator !=(BinaryKey? left, BinaryKey? right) => !Equals(left, right);
}
