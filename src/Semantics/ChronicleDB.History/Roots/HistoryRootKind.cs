namespace ChronicleDB.History.Roots;

/// <summary>
/// Logical kinds of entities that can retain a historical boundary.
/// </summary>
public enum HistoryRootKind : byte
{
    PersistentSnapshot = 1,
    BranchBase = 2,
    ActiveTransaction = 3,
}
