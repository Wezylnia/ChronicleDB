using ChronicleDB.Core.Keys;

namespace ChronicleDB.Indexing.Baseline;

/// <summary>
/// Understandable managed baseline used for correctness and later differential testing.
/// Reader/writer synchronization permits parallel lookups while preserving a simple
/// exclusive publication path.
/// </summary>
public sealed class SynchronizedVersionIndex : IVersionIndex, IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<BinaryKey, VersionHandle> _entries = [];
    private long _lookups;
    private long _publications;
    private long _removals;
    private long _contention;

    public int Count
    {
        get
        {
            EnterRead();
            try
            {
                return _entries.Count;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public bool TryGet(BinaryKey key, out VersionHandle head)
    {
        ArgumentNullException.ThrowIfNull(key);
        Interlocked.Increment(ref _lookups);
        EnterRead();
        try
        {
            return _entries.TryGetValue(key, out head);
        }
        finally
        {
            _gate.ExitReadLock();
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

        Interlocked.Increment(ref _publications);
        EnterWrite();
        try
        {
            _entries[key] = head;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryRemove(BinaryKey key, out VersionHandle head)
    {
        ArgumentNullException.ThrowIfNull(key);
        Interlocked.Increment(ref _removals);
        EnterWrite();
        try
        {
            return _entries.Remove(key, out head);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public VersionIndexStatistics GetStatistics()
        => new(
            Volatile.Read(ref _lookups),
            Volatile.Read(ref _publications),
            Volatile.Read(ref _removals),
            Volatile.Read(ref _contention));

    public void Dispose() => _gate.Dispose();

    private void EnterRead()
    {
        if (_gate.TryEnterReadLock(0))
        {
            return;
        }

        Interlocked.Increment(ref _contention);
        _gate.EnterReadLock();
    }

    private void EnterWrite()
    {
        if (_gate.TryEnterWriteLock(0))
        {
            return;
        }

        Interlocked.Increment(ref _contention);
        _gate.EnterWriteLock();
    }
}
