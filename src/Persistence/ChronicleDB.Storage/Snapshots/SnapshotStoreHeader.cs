using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Storage.Snapshots;

public sealed record SnapshotStoreHeader(
    Guid DatabaseId,
    CommitSequence RetentionFloor)
{
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;
    public const uint Crc32CAlgorithm = 1;
    public const uint MaxNameBytes = 1024;
}
