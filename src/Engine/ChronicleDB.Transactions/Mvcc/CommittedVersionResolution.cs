namespace ChronicleDB.Transactions.Mvcc;

public enum CommittedVersionResolutionKind : byte
{
    NoVisibleVersion = 0,
    Value = 1,
    Tombstone = 2,
}

public readonly record struct CommittedVersionResolution(
    CommittedVersionResolutionKind Kind,
    byte[] Value)
{
    public static CommittedVersionResolution Missing =>
        new(CommittedVersionResolutionKind.NoVisibleVersion, []);

    public static CommittedVersionResolution Deleted =>
        new(CommittedVersionResolutionKind.Tombstone, []);

    public static CommittedVersionResolution Present(byte[] value) =>
        new(CommittedVersionResolutionKind.Value, value);
}
