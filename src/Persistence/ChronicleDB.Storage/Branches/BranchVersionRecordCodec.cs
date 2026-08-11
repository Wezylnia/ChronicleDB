using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.Branches;

/// <summary>
/// Self-checking logical-version envelope stored as a value in a branch's
/// append-only PersistentKeyValueStore. The physical store key is deliberately
/// unrelated to the user's logical key so every historical version survives.
/// </summary>
public static class BranchVersionRecordCodec
{
    public const int HeaderSize = 88;
    private const byte CurrentVersion = 1;
    private const byte TombstoneFlag = 1;
    private static ReadOnlySpan<byte> Magic => "BVR1"u8;

    public static byte[] Encode(BranchVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        var totalLength = checked(HeaderSize + record.Key.Length + record.Value.Length);
        var buffer = new byte[totalLength];
        Magic.CopyTo(buffer);
        buffer[4] = CurrentVersion;
        buffer[5] = record.IsDelete ? TombstoneFlag : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), checked((uint)totalLength));
        record.BranchId.Value.TryWriteBytes(buffer.AsSpan(16, 16));
        record.HistoryId.Value.TryWriteBytes(buffer.AsSpan(32, 16));
        record.TransactionId.Value.TryWriteBytes(buffer.AsSpan(48, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(64, 8), record.CommitSequence.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(72, 4), checked((uint)record.MutationIndex));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(76, 4), checked((uint)record.MutationCount));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(80, 2), checked((ushort)record.Key.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(84, 4), checked((uint)record.Value.Length));
        record.Key.CopyTo(buffer.AsSpan(HeaderSize, record.Key.Length));
        record.Value.CopyTo(buffer.AsSpan(HeaderSize + record.Key.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(12, 4),
            Crc32C.ComputeWithZeroedRange(buffer, 12, 4));
        return buffer;
    }

    public static BranchVersionRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual(Magic))
        {
            throw new StorageFormatException("Branch version record framing is invalid.");
        }

        var version = bytes[4];
        var flags = bytes[5];
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        if (version != CurrentVersion
            || (flags & ~TombstoneFlag) != 0
            || headerSize != HeaderSize
            || totalLength != (uint)bytes.Length
            || expectedChecksum != Crc32C.ComputeWithZeroedRange(bytes, 12, 4))
        {
            throw new StorageCorruptionException("Branch version record header or checksum is invalid.");
        }

        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[80..82]);
        if (bytes[82..84].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new StorageFormatException("Branch version record reserved fields are non-zero.");
        }

        var mutationIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes[72..76]);
        var mutationCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[76..80]);
        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[84..88]);
        var remainingValueBytes = bytes.Length - HeaderSize - keyLength;
        if (mutationIndex > int.MaxValue
            || mutationCount > int.MaxValue
            || remainingValueBytes < 0
            || valueLength != (uint)remainingValueBytes)
        {
            throw new StorageCorruptionException("Branch version record lengths or mutation indexes are inconsistent.");
        }

        var record = new BranchVersionRecord(
            new BranchId(new Guid(bytes[16..32])),
            new HistoryId(new Guid(bytes[32..48])),
            new TransactionId(new Guid(bytes[48..64])),
            new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..72])),
            (int)mutationIndex,
            (int)mutationCount,
            bytes.Slice(HeaderSize, keyLength).ToArray(),
            (flags & TombstoneFlag) != 0,
            bytes.Slice(HeaderSize + keyLength, remainingValueBytes).ToArray());
        Validate(record);
        return record;
    }

    private static void Validate(BranchVersionRecord record)
    {
        if (!record.BranchId.IsValid
            || !record.HistoryId.IsValid
            || !record.TransactionId.IsValid
            || record.CommitSequence.IsInitial
            || record.MutationCount <= 0
            || record.MutationIndex < 0
            || record.MutationIndex >= record.MutationCount
            || record.Key.Length > ushort.MaxValue)
        {
            throw new StorageFormatException("Branch version record identity or sequence metadata is invalid.");
        }

        if (record.IsDelete && record.Value.Length != 0)
        {
            throw new StorageFormatException("Branch tombstone records cannot carry a value.");
        }
    }
}
