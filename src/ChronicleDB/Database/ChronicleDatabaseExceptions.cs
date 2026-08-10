namespace ChronicleDB;

public sealed class ChronicleDatabaseFaultedException : InvalidOperationException
{
    internal ChronicleDatabaseFaultedException()
        : base("The database is faulted after an uncertain durable operation and must be reopened for recovery.")
    {
    }
}
