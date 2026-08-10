namespace ChronicleDB.Storage.Faults;

public enum StorageFaultPoint
{
    BeforePageWrite = 0,
    AfterPageWrite = 1,
    BeforeSnapshotRecordWrite = 2,
    AfterSnapshotRecordWrite = 3,
    BeforeSnapshotFlush = 4,
    AfterSnapshotFlush = 5
}
