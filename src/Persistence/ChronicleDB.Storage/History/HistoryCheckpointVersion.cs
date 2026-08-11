using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Storage.History;

/// <summary>
/// One immutable committed MVCC version retained in a durable history checkpoint.
/// A checkpoint may intentionally omit versions that are unreachable from the
/// advertised time-travel floor and every explicit historical root.
/// </summary>
public sealed record HistoryCheckpointVersion(
    TransactionId TransactionId,
    CommitSequence CommitSequence,
    BinaryKey Key,
    bool IsDelete,
    ReadOnlyMemory<byte> Value);

public sealed record HistoryCheckpoint(
    Guid DatabaseId,
    HistoryId HistoryId,
    CommitSequence CheckpointSequence,
    CommitSequence RetentionFloor,
    IReadOnlyList<HistoryCheckpointVersion> Versions);
