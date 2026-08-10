namespace ChronicleDB.History.Roots;

/// <summary>
/// Crash-recovery-visible lifecycle of a historical root.
/// </summary>
public enum HistoryRootState : byte
{
    Creating = 1,
    Active = 2,
    Deleting = 3,
    Deleted = 4,
}
