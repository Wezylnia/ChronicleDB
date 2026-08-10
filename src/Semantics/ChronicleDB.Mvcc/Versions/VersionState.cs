namespace ChronicleDB.Mvcc.Versions;

public enum VersionState
{
    Pending = 0,
    Committed = 1,
    Aborted = 2
}
