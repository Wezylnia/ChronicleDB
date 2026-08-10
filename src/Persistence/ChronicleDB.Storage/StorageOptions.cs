namespace ChronicleDB.Storage;

using ChronicleDB.Storage.Faults;

public sealed record StorageOptions
{
    public const int DefaultPageSize = 16 * 1024;
    public const int DefaultMaxKeySize = 1024;
    public const int DefaultMaxValueSize = 64 * 1024 * 1024;
    public const int AbsoluteMaxValueSize = 256 * 1024 * 1024;

    public int PageSize { get; init; } = DefaultPageSize;

    public int MaxKeySize { get; init; } = DefaultMaxKeySize;

    public int MaxValueSize { get; init; } = DefaultMaxValueSize;

    public bool FlushOnWrite { get; init; } = true;

    /// <summary>
    /// Test-only durable-storage fault injection for data pages and snapshot metadata;
    /// null in production.
    /// </summary>
    public IStorageFaultInjector? FaultInjector { get; init; }

    public int InlineValueCapacity(int keyLength)
    {
        Validate();

        if (keyLength is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(keyLength));
        }

        return checked(PageSize - Pages.PageHeader.Size - Records.RecordCodec.HeaderSize - keyLength);
    }

    internal void Validate()
    {
        if (PageSize != DefaultPageSize)
        {
            throw new StorageFormatException(
                $"Only the v0.1 page size of {DefaultPageSize} bytes is supported.");
        }

        if (MaxKeySize is <= 0 or > ushort.MaxValue)
        {
            throw new StorageLimitException(
                $"MaxKeySize must be between 1 and {ushort.MaxValue} bytes.");
        }

        if (MaxValueSize is <= 0 or > AbsoluteMaxValueSize)
        {
            throw new StorageLimitException(
                $"MaxValueSize must be between 1 and {AbsoluteMaxValueSize} bytes.");
        }

        var inlineCapacity = checked(PageSize - Pages.PageHeader.Size - Records.RecordCodec.HeaderSize - MaxKeySize);
        if (inlineCapacity <= 0)
        {
            throw new StorageLimitException("The configured key limit leaves no record payload capacity.");
        }
    }
}
