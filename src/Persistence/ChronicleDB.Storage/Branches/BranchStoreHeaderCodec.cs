using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.Branches;

public static class BranchStoreHeaderCodec
{
    public const int Size = 64;
    private static ReadOnlySpan<byte> Magic => "CHBRN001"u8;

    public static byte[] Encode(BranchStoreHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.DatabaseId == Guid.Empty || !header.MainHistoryId.IsValid)
        {
            throw new StorageFormatException("A branch-store header requires valid database and Main-history identities.");
        }

        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), BranchStoreHeader.CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), BranchStoreHeader.CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), Size);
        header.DatabaseId.TryWriteBytes(buffer.AsSpan(16, 16));
        header.MainHistoryId.Value.TryWriteBytes(buffer.AsSpan(32, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48, 4), BranchStoreHeader.Crc32CAlgorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60, 4), Crc32C.Compute(buffer.AsSpan(0, 60)));
        return buffer;
    }

    public static BranchStoreHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size || !bytes[..8].SequenceEqual(Magic))
        {
            throw new StorageFormatException("Branch-store header framing is invalid.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..64]);
        if (expected != Crc32C.Compute(bytes[..60]))
        {
            throw new StorageCorruptionException("Branch-store header checksum is invalid.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var algorithm = BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..52]);
        if (major != BranchStoreHeader.CurrentMajorVersion
            || minor > BranchStoreHeader.CurrentMinorVersion
            || size != Size
            || algorithm != BranchStoreHeader.Crc32CAlgorithm
            || bytes[52..60].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new StorageFormatException("Branch-store header contains unsupported fields.");
        }

        var databaseId = new Guid(bytes[16..32]);
        var mainHistoryId = new HistoryId(new Guid(bytes[32..48]));
        if (databaseId == Guid.Empty || !mainHistoryId.IsValid)
        {
            throw new StorageFormatException("Branch-store header contains invalid identities.");
        }

        return new BranchStoreHeader(databaseId, mainHistoryId);
    }
}
