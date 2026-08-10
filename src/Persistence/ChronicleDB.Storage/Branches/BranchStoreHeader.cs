using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Storage.Branches;

public sealed record BranchStoreHeader(Guid DatabaseId, HistoryId MainHistoryId)
{
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;
    public const uint Crc32CAlgorithm = 1;
}
