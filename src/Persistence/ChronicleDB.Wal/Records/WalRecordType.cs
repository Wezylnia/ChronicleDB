namespace ChronicleDB.Wal.Records;

public enum WalRecordType : byte
{
    Begin = 1,
    Put = 2,
    Delete = 3,
    Commit = 4,
    Abort = 5
}
