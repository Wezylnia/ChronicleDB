namespace ChronicleDB.Maintenance;

public sealed record CompactionResult(
    int HistoriesCompacted,
    long BytesBefore,
    long BytesAfter,
    long BytesReclaimed,
    long BytesRewritten);
