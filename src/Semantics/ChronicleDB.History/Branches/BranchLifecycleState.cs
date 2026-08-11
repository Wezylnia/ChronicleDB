namespace ChronicleDB.History.Branches;

/// <summary>
/// Persistent branch lifecycle states used by the durable metadata journal.
/// Only Active branches are externally openable; incomplete create/delete intents are reconciled during open.
/// </summary>
public enum BranchLifecycleState : byte
{
    Creating = 1,
    Active = 2,
    Deleting = 3,
    Deleted = 4,
}
