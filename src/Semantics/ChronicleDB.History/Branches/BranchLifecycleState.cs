namespace ChronicleDB.History.Branches;

/// <summary>
/// Persistent branch metadata lifecycle. v0.7 exposes only Active branches;
/// Creating entries are durable intents resolved during open.
/// </summary>
public enum BranchLifecycleState : byte
{
    Creating = 1,
    Active = 2,
    Deleting = 3,
    Deleted = 4,
}
