namespace ChronicleDB.Storage.Formats;

public sealed record DatabaseHeader(
    Guid DatabaseId,
    int PageSize,
    uint FormatFlags,
    long CreatedUnixMilliseconds)
{
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;
    public const uint Crc32CAlgorithm = 1;
}
