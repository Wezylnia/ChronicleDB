namespace ChronicleDB.Transactions.State;

public enum TransactionState
{
    Created = 0,
    Active = 1,
    Preparing = 2,
    Committing = 3,
    Committed = 4,
    Aborting = 5,
    Aborted = 6
}
