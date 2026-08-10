using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Storage.HistoryRoots;

public sealed record HistoryRootStoreHeader(
    Guid DatabaseId,
    HistoryId MainHistoryId)
{
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;
    public const uint Crc32CAlgorithm = 1;
}
