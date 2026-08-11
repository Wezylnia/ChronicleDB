namespace ChronicleDB.Storage.Formats;

public sealed record DatabaseHeader(
    Guid DatabaseId,
    int PageSize,
    uint FormatFlags,
    long CreatedUnixMilliseconds,
    ulong Generation = 1)
{
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 4;
    public const uint Crc32CAlgorithm = 1;

    public const uint WalInitializedFlag = 1u << 0;
    public const uint SnapshotStoreInitializedFlag = 1u << 1;
    public const uint HistoryRootStoreInitializedFlag = 1u << 2;
    public const uint BranchStoreInitializedFlag = 1u << 3;
    public const uint HistoryCheckpointInitializedFlag = 1u << 4;
    public const uint SupportedFormatFlags =
        WalInitializedFlag
        | SnapshotStoreInitializedFlag
        | HistoryRootStoreInitializedFlag
        | BranchStoreInitializedFlag
        | HistoryCheckpointInitializedFlag;
}
