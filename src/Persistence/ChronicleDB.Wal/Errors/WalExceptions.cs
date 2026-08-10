namespace ChronicleDB.Wal.Errors;

public class WalException : Exception
{
    public WalException(string message)
        : base(message)
    {
    }

    public WalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WalFormatException : WalException
{
    public WalFormatException(string message)
        : base(message)
    {
    }
}

public sealed class WalCorruptionException : WalException
{
    public WalCorruptionException(string message)
        : base(message)
    {
    }
}

public sealed class WalLimitException : WalException
{
    public WalLimitException(string message)
        : base(message)
    {
    }
}
