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

public sealed class SnapshotNotFoundException : KeyNotFoundException
{
    internal SnapshotNotFoundException(string identity)
        : base($"Persistent snapshot {identity} does not exist.")
    {
    }
}

public sealed class SnapshotNameConflictException : InvalidOperationException
{
    internal SnapshotNameConflictException(string name)
        : base($"A persistent snapshot named '{name}' already exists.")
    {
    }
}

public sealed class HistoricalStateUnavailableException : InvalidOperationException
{
    internal HistoricalStateUnavailableException(ulong requested, ulong retentionFloor, ulong current)
        : base(
            $"Historical sequence {requested} is outside the retained range " +
            $"[{retentionFloor}, {current}].")
    {
        RequestedSequence = requested;
        RetentionFloor = retentionFloor;
        CurrentSequence = current;
    }

    public ulong RequestedSequence { get; }

    public ulong RetentionFloor { get; }

    public ulong CurrentSequence { get; }
}

public sealed class BranchNotFoundException : KeyNotFoundException
{
    internal BranchNotFoundException(string identity)
        : base($"Branch {identity} does not exist.")
    {
    }
}

public sealed class BranchNameConflictException : InvalidOperationException
{
    internal BranchNameConflictException(string name)
        : base($"A branch named '{name}' already exists.")
    {
    }
}

public sealed class BranchHistoricalStateUnavailableException : InvalidOperationException
{
    internal BranchHistoricalStateUnavailableException(Guid branchId, ulong requested, ulong current)
        : base($"Branch {branchId} historical sequence {requested} is newer than local sequence {current}.")
    {
        BranchId = branchId;
        RequestedSequence = requested;
        CurrentSequence = current;
    }

    public Guid BranchId { get; }

    public ulong RequestedSequence { get; }

    public ulong CurrentSequence { get; }
}
