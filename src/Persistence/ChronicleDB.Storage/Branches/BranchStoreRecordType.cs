namespace ChronicleDB.Storage.Branches;

public enum BranchStoreRecordType : byte
{
    CreateIntent = 1,
    Activate = 2,
    AdvanceSequence = 3,
    AbandonCreate = 4,
}
