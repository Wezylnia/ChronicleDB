namespace ChronicleDB.Core.Keys;

/// <summary>
/// Byte-wise lexicographic ordering for immutable binary keys.
/// </summary>
public sealed class BinaryKeyLexicographicComparer : IComparer<BinaryKey>
{
    public static BinaryKeyLexicographicComparer Instance { get; } = new();

    private BinaryKeyLexicographicComparer()
    {
    }

    public int Compare(BinaryKey? x, BinaryKey? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }

        return x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
