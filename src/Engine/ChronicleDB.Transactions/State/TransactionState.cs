namespace ChronicleDB.Transactions.State;

public enum TransactionState
{
    Created = 0,
    Active = 1,
    Preparing = 2,
    Committing = 3,
    /// <summary> The WAL commit record has been flushed durably, but physical publication is not complete. </summary>
    DurableCommitted = 4,
    Committed = 5,
    Aborting = 6,
    Aborted = 7
}
