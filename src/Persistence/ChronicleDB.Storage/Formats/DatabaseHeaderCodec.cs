using System.Buffers.Binary;

namespace ChronicleDB.Storage.Formats;

public static class DatabaseHeaderCodec
{
    public const int Size = 64;

    private static ReadOnlySpan<byte> Magic => "CHDBv001"u8;

    public static byte[] Encode(DatabaseHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (header.DatabaseId == Guid.Empty)
        {
            throw new StorageFormatException("A database header requires a non-empty database ID.");
        }

        if (header.PageSize != StorageOptions.DefaultPageSize)
        {
            throw new StorageFormatException("The v0.1 database header must use a 16 KiB page size.");
        }

        if (header.FormatFlags != 0)
        {
            throw new StorageFormatException("The v0.1 database header does not define format flags.");
        }

        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), DatabaseHeader.CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), DatabaseHeader.CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), Size);
        header.DatabaseId.TryWriteBytes(buffer.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32, 4), checked((uint)header.PageSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36, 4), DatabaseHeader.Crc32CAlgorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), header.FormatFlags);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(44, 8), header.CreatedUnixMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60, 4), Crc32C.Compute(buffer.AsSpan(0, 60)));
        return buffer;
    }

    public static DatabaseHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new StorageCorruptionException($"Database header must be exactly {Size} bytes.");
        }

        if (!bytes[..8].SequenceEqual(Magic))
        {
            throw new StorageFormatException("Database header magic is not recognized.");
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..64]);
        var actualChecksum = Crc32C.Compute(bytes[..60]);
        if (expectedChecksum != actualChecksum)
        {
            throw new StorageCorruptionException("Database header checksum is invalid.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..36]);
        var checksumAlgorithm = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..40]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..44]);
        var created = BinaryPrimitives.ReadInt64LittleEndian(bytes[44..52]);
        var reserved = BinaryPrimitives.ReadUInt64LittleEndian(bytes[52..60]);

        if (major != DatabaseHeader.CurrentMajorVersion || minor > DatabaseHeader.CurrentMinorVersion)
        {
            throw new StorageFormatException($"Unsupported database format version {major}.{minor}.");
        }

        if (headerSize != Size)
        {
            throw new StorageFormatException("Database header size is not supported.");
        }

        if (pageSize != StorageOptions.DefaultPageSize)
        {
            throw new StorageFormatException("Database page size is not supported.");
        }

        if (checksumAlgorithm != DatabaseHeader.Crc32CAlgorithm)
        {
            throw new StorageFormatException("Database checksum algorithm is not supported.");
        }

        if (flags != 0)
        {
            throw new StorageFormatException("Database header contains unsupported format flags.");
        }

        if (reserved != 0)
        {
            throw new StorageFormatException("Database header reserved bytes are not zero.");
        }

        var databaseId = new Guid(bytes[16..32]);
        if (databaseId == Guid.Empty)
        {
            throw new StorageFormatException("Database header contains an empty database ID.");
        }

        return new DatabaseHeader(databaseId, checked((int)pageSize), flags, created);
    }
}
