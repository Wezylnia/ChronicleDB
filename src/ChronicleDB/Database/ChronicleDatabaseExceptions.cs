namespace ChronicleDB;

public sealed class ChronicleDatabaseFaultedException : InvalidOperationException
{
    internal ChronicleDatabaseFaultedException()
        : base("The database is faulted after an uncertain durable operation and must be reopened for recovery.")
    {
    }
}

public sealed class TransactionConflictException : InvalidOperationException
{
    internal TransactionConflictException(
        Guid transactionId,
        ulong startSequence,
        ulong conflictingSequence)
        : base(
            $"Transaction {transactionId} started at sequence {startSequence} but a written key " +
            $"was changed by committed sequence {conflictingSequence}.")
    {
        TransactionId = transactionId;
        StartSequence = startSequence;
        ConflictingSequence = conflictingSequence;
    }

    public Guid TransactionId { get; }

    public ulong StartSequence { get; }

    public ulong ConflictingSequence { get; }
}
