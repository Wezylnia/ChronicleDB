using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;

namespace ChronicleDB.Storage.Records;

internal static class RecordCodec
{
    public const int HeaderSize = 24;
    private const byte RecordVersion = 1;
    private const byte TombstoneFlag = 1;
    private const byte OverflowFlag = 2;
    private const byte KnownFlags = TombstoneFlag | OverflowFlag;

    public static byte[] Encode(
        BinaryKey key,
        ReadOnlySpan<byte> value,
        PageId overflowHead,
        bool tombstone,
        StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateKey(key, options);

        if (value.Length > options.MaxValueSize)
        {
            throw new StorageLimitException("Value exceeds the configured maximum size.");
        }

        if (tombstone && (value.Length != 0 || overflowHead.IsValid))
        {
            throw new StorageFormatException("A tombstone cannot contain a value.");
        }

        if (overflowHead.IsValid && value.Length == 0)
        {
            throw new StorageFormatException("An overflow record must declare a non-empty value.");
        }

        var inlineLength = overflowHead.IsValid ? 0 : value.Length;
        var payloadLength = checked(HeaderSize + key.Length + inlineLength);
        if (payloadLength > options.PageSize - Pages.PageHeader.Size)
        {
            throw new StorageLimitException("Record metadata and inline value do not fit in one page.");
        }

        var payload = new byte[payloadLength];
        payload[0] = RecordVersion;
        payload[1] = (byte)((tombstone ? TombstoneFlag : 0) | (overflowHead.IsValid ? OverflowFlag : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)key.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)value.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), overflowHead.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), checked((uint)inlineLength));
        key.AsSpan().CopyTo(payload.AsSpan(HeaderSize));
        value[..inlineLength].CopyTo(payload.AsSpan(HeaderSize + key.Length));
        return payload;
    }

    public static DecodedRecord Decode(ReadOnlySpan<byte> payload, StorageOptions options)
    {
        if (payload.Length < HeaderSize)
        {
            throw new StorageCorruptionException("Record payload is shorter than its header.");
        }

        var version = payload[0];
        var flags = payload[1];
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..4]);
        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        var overflowHeadValue = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..16]);
        var inlineLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..20]);
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(payload[20..24]);

        if (version != RecordVersion || (flags & ~KnownFlags) != 0 || reserved != 0)
        {
            throw new StorageCorruptionException("Record header contains unsupported fields.");
        }

        if (keyLength > options.MaxKeySize || valueLength > options.MaxValueSize)
        {
            throw new StorageCorruptionException("Record lengths exceed the configured storage limits.");
        }

        var requiredLength = checked(HeaderSize + keyLength + inlineLength);
        if (requiredLength != payload.Length)
        {
            throw new StorageCorruptionException("Record payload length does not match its fields.");
        }

        var tombstone = (flags & TombstoneFlag) != 0;
        var hasOverflow = (flags & OverflowFlag) != 0;
        var overflowHead = new PageId(overflowHeadValue);

        if (tombstone && (valueLength != 0 || inlineLength != 0 || hasOverflow))
        {
            throw new StorageCorruptionException("Tombstone record contains a value.");
        }

        if (hasOverflow && (!overflowHead.IsValid || inlineLength != 0 || valueLength == 0))
        {
            throw new StorageCorruptionException("Overflow record fields are inconsistent.");
        }

        if (!hasOverflow && (overflowHead.IsValid || inlineLength != valueLength))
        {
            throw new StorageCorruptionException("Inline record fields are inconsistent.");
        }

        var key = new BinaryKey(payload.Slice(HeaderSize, keyLength));
        var inlineValue = payload.Slice(HeaderSize + keyLength, checked((int)inlineLength)).ToArray();
        return new DecodedRecord(key, checked((int)valueLength), overflowHead, tombstone, inlineValue);
    }

    private static void ValidateKey(BinaryKey key, StorageOptions options)
    {
        if (key.Length > options.MaxKeySize)
        {
            throw new StorageLimitException("Key exceeds the configured maximum size.");
        }
    }
}

internal sealed record DecodedRecord(
    BinaryKey Key,
    int ValueLength,
    PageId OverflowHead,
    bool IsTombstone,
    byte[] InlineValue);
