using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.HistoryRoots;

public static class HistoryRootStoreHeaderCodec
{
    public const int Size = 64;
    private static ReadOnlySpan<byte> Magic => "CHROOT01"u8;

    public static byte[] Encode(HistoryRootStoreHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.DatabaseId == Guid.Empty || !header.MainHistoryId.IsValid)
        {
            throw new StorageFormatException("A history-root header requires valid database and history identities.");
        }

        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), HistoryRootStoreHeader.CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), HistoryRootStoreHeader.CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), Size);
        header.DatabaseId.TryWriteBytes(buffer.AsSpan(16, 16));
        header.MainHistoryId.Value.TryWriteBytes(buffer.AsSpan(32, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48, 4), HistoryRootStoreHeader.Crc32CAlgorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60, 4), Crc32C.Compute(buffer.AsSpan(0, 60)));
        return buffer;
    }

    public static HistoryRootStoreHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new StorageCorruptionException($"History-root header must be exactly {Size} bytes.");
        }

        if (!bytes[..8].SequenceEqual(Magic))
        {
            throw new StorageFormatException("History-root header magic is not recognized.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..64]);
        if (expected != Crc32C.Compute(bytes[..60]))
        {
            throw new StorageCorruptionException("History-root header checksum is invalid.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var algorithm = BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..52]);
        if (major != HistoryRootStoreHeader.CurrentMajorVersion
            || minor > HistoryRootStoreHeader.CurrentMinorVersion
            || headerSize != Size
            || algorithm != HistoryRootStoreHeader.Crc32CAlgorithm
            || bytes[52..60].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new StorageFormatException("History-root header contains unsupported fields.");
        }

        var databaseId = new Guid(bytes[16..32]);
        var mainHistoryId = new HistoryId(new Guid(bytes[32..48]));
        if (databaseId == Guid.Empty || !mainHistoryId.IsValid)
        {
            throw new StorageFormatException("History-root header contains an invalid identity.");
        }

        return new HistoryRootStoreHeader(databaseId, mainHistoryId);
    }
}
