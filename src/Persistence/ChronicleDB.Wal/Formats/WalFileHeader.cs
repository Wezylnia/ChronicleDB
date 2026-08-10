namespace ChronicleDB.Wal.Formats;

public sealed record WalFileHeader(Guid DatabaseId)
{
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;
    public const uint Crc32CAlgorithm = 1;
}
