using System.Buffers.Binary;
using ChronicleDB.Wal.Errors;

namespace ChronicleDB.Wal.Formats;

public static class WalFileHeaderCodec
{
    public const int Size = 64;
    private static ReadOnlySpan<byte> Magic => "CWLHDR01"u8;

    public static byte[] Encode(WalFileHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.DatabaseId == Guid.Empty)
        {
            throw new WalFormatException("A WAL header requires a non-empty database ID.");
        }

        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), WalFileHeader.CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), WalFileHeader.CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), Size);
        header.DatabaseId.TryWriteBytes(buffer.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(32, 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), WalFileHeader.Crc32CAlgorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60, 4), Crc32C.Compute(buffer.AsSpan(0, 60)));
        return buffer;
    }

    public static WalFileHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new WalCorruptionException($"WAL file header must be exactly {Size} bytes.");
        }

        if (!bytes[..8].SequenceEqual(Magic))
        {
            throw new WalFormatException("WAL file header magic is not recognized.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..64]);
        if (expected != Crc32C.Compute(bytes[..60]))
        {
            throw new WalCorruptionException("WAL file header checksum is invalid.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var firstLsn = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..40]);
        var algorithm = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..44]);
        var reserved = bytes[44..60];
        if (major != WalFileHeader.CurrentMajorVersion || minor > WalFileHeader.CurrentMinorVersion
            || headerSize != Size || firstLsn != 1 || algorithm != WalFileHeader.Crc32CAlgorithm
            || reserved.IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new WalFormatException("WAL file header contains unsupported fields.");
        }

        var databaseId = new Guid(bytes[16..32]);
        if (databaseId == Guid.Empty)
        {
            throw new WalFormatException("WAL file header contains an empty database ID.");
        }

        return new WalFileHeader(databaseId);
    }
}
