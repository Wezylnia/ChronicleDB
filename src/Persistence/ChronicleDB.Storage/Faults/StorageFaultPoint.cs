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
    AfterBranchMetadataFlush = 13
}
