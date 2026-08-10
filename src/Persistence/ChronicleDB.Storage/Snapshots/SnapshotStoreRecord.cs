using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Storage.Snapshots;

public sealed record SnapshotStoreRecord(
    SnapshotStoreRecordType Type,
    ulong EventSequence,
    SnapshotId SnapshotId,
    CommitSequence Sequence,
    long CreatedUnixMilliseconds,
    string Name);
