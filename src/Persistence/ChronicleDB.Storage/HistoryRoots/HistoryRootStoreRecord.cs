using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Storage.HistoryRoots;

/// <summary>
/// Storage-neutral representation of one root lifecycle event. Root kind/state
/// values are interpreted by the History semantic assembly, keeping Storage
/// independent from that assembly's dependency direction.
/// </summary>
public sealed record HistoryRootStoreRecord(
    HistoryRootStoreRecordType Type,
    ulong EventSequence,
    HistoryRootId RootId,
    byte RootKind,
    byte RootState,
    Guid OwnerDatabaseId,
    HistoryId HistoryId,
    HistoryId ParentHistoryId,
    CommitSequence Boundary,
    long CreatedUnixMilliseconds);
