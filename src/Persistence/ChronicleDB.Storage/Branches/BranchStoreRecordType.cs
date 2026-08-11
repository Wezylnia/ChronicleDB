namespace ChronicleDB.Storage.Branches;

public enum BranchStoreRecordType : byte
{
    CreateIntent = 1,
    Activate = 2,
    AdvanceSequence = 3,
    AbandonCreate = 4,
    DeleteIntent = 5,
    DeleteComplete = 6,
    PublishPhysicalBoundary = 7,
    RestoreActive = 8,
}
