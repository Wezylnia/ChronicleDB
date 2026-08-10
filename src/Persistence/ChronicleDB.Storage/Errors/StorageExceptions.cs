namespace ChronicleDB.Storage;

public class StorageException : Exception
{
    public StorageException(string message)
        : base(message)
    {
    }

    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class StorageFormatException : StorageException
{
    public StorageFormatException(string message)
        : base(message)
    {
    }
}

public sealed class StorageCorruptionException : StorageException
{
    public StorageCorruptionException(string message)
        : base(message)
    {
    }
}

public sealed class StorageLimitException : StorageException
{
    public StorageLimitException(string message)
        : base(message)
    {
    }
}
