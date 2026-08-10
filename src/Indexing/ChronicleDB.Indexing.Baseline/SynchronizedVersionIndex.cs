using ChronicleDB.Core.Keys;

namespace ChronicleDB.Indexing.Baseline;

/// <summary>
/// Understandable managed baseline used for correctness and later differential testing.
/// </summary>
public sealed class SynchronizedVersionIndex : IVersionIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<BinaryKey, VersionHandle> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(BinaryKey key, out VersionHandle head)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            return _entries.TryGetValue(key, out head);
        }
    }

    public void Publish(BinaryKey key, VersionHandle head)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!head.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(head),
                "An index entry must point to a valid version handle.");
        }

        lock (_gate)
        {
            _entries[key] = head;
        }
    }

    public bool TryRemove(BinaryKey key, out VersionHandle head)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            return _entries.Remove(key, out head);
        }
    }
}
