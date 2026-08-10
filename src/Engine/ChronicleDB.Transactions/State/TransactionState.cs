namespace ChronicleDB.Transactions.State;

public enum TransactionState
{
    Created = 0,
    Active = 1,
    Preparing = 2,
    Committing = 3,
    DurableCommitted = 4,
    Committed = 5,
    Aborting = 6,
    Aborted = 7,
    /// <summary>
    /// WAL I/O was touched but the caller cannot prove whether the durable commit
    /// boundary was crossed. Reopen/recovery is authoritative; local abort is illegal.
    /// </summary>
    Indeterminate = 8
}
