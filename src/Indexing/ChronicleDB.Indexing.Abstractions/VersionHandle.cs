namespace ChronicleDB.Indexing;

/// <summary>
/// An opaque logical handle to a version-chain head.
/// </summary>
public readonly record struct VersionHandle(ulong Value)
{
    public static VersionHandle Invalid => new(0);

    public bool IsValid => Value != 0;
}
