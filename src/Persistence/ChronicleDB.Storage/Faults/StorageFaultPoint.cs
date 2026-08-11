namespace ChronicleDB.Storage.Faults;

public enum StorageFaultPoint
{
    BeforePageWrite = 0,
    AfterPageWrite = 1,
    BeforeSnapshotRecordWrite = 2,
    AfterSnapshotRecordWrite = 3,
    BeforeSnapshotFlush = 4,
    AfterSnapshotFlush = 5,
    BeforeHistoryRootRecordWrite = 6,
    AfterHistoryRootRecordWrite = 7,
    BeforeHistoryRootFlush = 8,
    AfterHistoryRootFlush = 9,
    BeforeBranchMetadataRecordWrite = 10,
    AfterBranchMetadataRecordWrite = 11,
    BeforeBranchMetadataFlush = 12,
    AfterBranchMetadataFlush = 13,
    BeforeCompactionPublish = 14,
    AfterCompactionPublish = 15,
    BeforeCompactionCleanup = 16,
    AfterCompactionCleanup = 17,
    BeforeHistoryCheckpointWrite = 18,
    AfterHistoryCheckpointOutputFlush = 19,
    BeforeHistoryWalReset = 20,
    AfterHistoryWalReset = 21,
    AfterHistoryCheckpointHeaderWrite = 22,
    AfterHistoryCheckpointRecordWrite = 23
}
