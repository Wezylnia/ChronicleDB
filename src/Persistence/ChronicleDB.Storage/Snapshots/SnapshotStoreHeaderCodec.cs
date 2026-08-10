using System.Buffers.Binary;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.Snapshots;

public static class SnapshotStoreHeaderCodec
{
    public const int Size = 64;
    private static ReadOnlySpan<byte> Magic => "CHSNAP01"u8;

    public static byte[] Encode(SnapshotStoreHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.DatabaseId == Guid.Empty)
        {
            throw new StorageFormatException("A snapshot store header requires a non-empty database ID.");
        }

        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), SnapshotStoreHeader.CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), SnapshotStoreHeader.CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), Size);
        header.DatabaseId.TryWriteBytes(buffer.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(32, 8), header.RetentionFloor.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), SnapshotStoreHeader.Crc32CAlgorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(44, 4), SnapshotStoreHeader.MaxNameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60, 4), Crc32C.Compute(buffer.AsSpan(0, 60)));
        return buffer;
    }

    public static SnapshotStoreHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new StorageCorruptionException($"Snapshot store header must be exactly {Size} bytes.");
        }

        if (!bytes[..8].SequenceEqual(Magic))
        {
            throw new StorageFormatException("Snapshot store header magic is not recognized.");
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..64]);
        if (expectedChecksum != Crc32C.Compute(bytes[..60]))
        {
            throw new StorageCorruptionException("Snapshot store header checksum is invalid.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var retentionFloor = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..40]);
        var checksumAlgorithm = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..44]);
        var maxNameBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..48]);
        var reserved = bytes[48..60];

        if (major != SnapshotStoreHeader.CurrentMajorVersion
            || minor > SnapshotStoreHeader.CurrentMinorVersion
            || headerSize != Size
            || checksumAlgorithm != SnapshotStoreHeader.Crc32CAlgorithm
            || maxNameBytes != SnapshotStoreHeader.MaxNameBytes
            || reserved.IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new StorageFormatException("Snapshot store header contains unsupported fields.");
        }

        var databaseId = new Guid(bytes[16..32]);
        if (databaseId == Guid.Empty)
        {
            throw new StorageFormatException("Snapshot store header contains an empty database ID.");
        }

        return new SnapshotStoreHeader(databaseId, new CommitSequence(retentionFloor));
    }
}
