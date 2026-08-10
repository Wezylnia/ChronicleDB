using ChronicleDB.Core.Keys;

namespace ChronicleDB.ReferenceModel;

/// <summary>
/// Intentionally simple Snapshot Isolation oracle. It uses full managed copies,
/// a single logical commit order, and linear version scans rather than sharing
/// implementation details with ChronicleDB.
/// </summary>
public sealed class ReferenceMvccModel
{
    private readonly object _gate = new();
    private readonly Dictionary<BinaryKey, List<ReferenceVersion>> _versions = [];
    private readonly Dictionary<string, ReferenceSnapshot> _snapshots = new(StringComparer.Ordinal);
    private ulong _currentCommitSequence;

    public ulong CurrentCommitSequence
    {
        get
        {
            lock (_gate)
            {
                return _currentCommitSequence;
            }
        }
    }

    public ReferenceTransaction BeginTransaction()
    {
        lock (_gate)
        {
            return new ReferenceTransaction(this, _currentCommitSequence);
        }
    }

    public ReferenceSnapshot CreateSnapshot(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_snapshots.ContainsKey(name))
            {
                throw new InvalidOperationException($"Reference snapshot '{name}' already exists.");
            }

            var snapshot = new ReferenceSnapshot(name, _currentCommitSequence);
            _snapshots.Add(name, snapshot);
            return snapshot;
        }
    }

    public void DeleteSnapshot(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (!_snapshots.Remove(name))
            {
                throw new KeyNotFoundException($"Reference snapshot '{name}' does not exist.");
            }
        }
    }

    public IReadOnlyList<ReferenceSnapshot> ListSnapshots()
    {
        lock (_gate)
        {
            return _snapshots.Values.OrderBy(item => item.Sequence).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }
    }

    public bool TryReadHistorical(ReadOnlySpan<byte> key, ulong boundary, out byte[] value)
    {
        lock (_gate)
        {
            if (boundary > _currentCommitSequence)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(boundary, _currentCommitSequence, nameof(boundary));
            }
        }

        return TryReadAt(key, boundary, out value);
    }

    internal bool TryReadAt(ReadOnlySpan<byte> key, ulong boundary, out byte[] value)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(new BinaryKey(key), out var chain))
            {
                value = [];
                return false;
            }

            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var version = chain[index];
                if (version.CommitSequence > boundary)
                {
                    continue;
                }

                if (version.IsDelete)
                {
                    value = [];
                    return false;
                }

                value = version.Value.ToArray();
                return true;
            }

            value = [];
            return false;
        }
    }

    internal ulong Commit(ReferenceTransaction transaction, IReadOnlyDictionary<BinaryKey, ReferenceWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(writes);

        lock (_gate)
        {
            foreach (var write in writes.Values)
            {
                if (_versions.TryGetValue(write.Key, out var chain)
                    && chain.Count != 0
                    && chain[^1].CommitSequence > transaction.StartSequence)
                {
                    throw new ReferenceTransactionConflictException(
                        transaction.StartSequence,
                        chain[^1].CommitSequence);
                }
            }

            _currentCommitSequence = checked(_currentCommitSequence + 1);
            foreach (var write in writes.Values)
            {
                if (!_versions.TryGetValue(write.Key, out var chain))
                {
                    chain = [];
                    _versions.Add(write.Key, chain);
                }

                chain.Add(new ReferenceVersion(
                    _currentCommitSequence,
                    write.IsDelete,
                    write.IsDelete ? [] : write.Value.ToArray()));
            }

            return _currentCommitSequence;
        }
    }

    private sealed record ReferenceVersion(
        ulong CommitSequence,
        bool IsDelete,
        byte[] Value);
}

public sealed class ReferenceTransaction : IDisposable
{
    private readonly ReferenceMvccModel _model;
    private readonly Dictionary<BinaryKey, ReferenceWrite> _writes = [];
    private bool _completed;

    internal ReferenceTransaction(ReferenceMvccModel model, ulong startSequence)
    {
        _model = model;
        StartSequence = startSequence;
    }

    public ulong StartSequence { get; }

    public ulong? CommitSequence { get; private set; }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ThrowIfCompleted();
        var ownedKey = new BinaryKey(key);
        _writes[ownedKey] = new ReferenceWrite(ownedKey, isDelete: false, value);
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        ThrowIfCompleted();
        var ownedKey = new BinaryKey(key);
        _writes[ownedKey] = new ReferenceWrite(ownedKey, isDelete: true, ReadOnlySpan<byte>.Empty);
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfCompleted();
        if (_writes.TryGetValue(new BinaryKey(key), out var local))
        {
            if (local.IsDelete)
            {
                value = [];
                return false;
            }

            value = local.Value.ToArray();
            return true;
        }

        return _model.TryReadAt(key, StartSequence, out value);
    }

    public ulong Commit()
    {
        ThrowIfCompleted();
        try
        {
            CommitSequence = _model.Commit(this, _writes);
            return CommitSequence.Value;
        }
        finally
        {
            _completed = true;
            _writes.Clear();
        }
    }

    public void Abort()
    {
        ThrowIfCompleted();
        _writes.Clear();
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed)
        {
            _writes.Clear();
            _completed = true;
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Reference transaction is already complete.");
        }
    }
}

public sealed class ReferenceWrite
{
    private readonly byte[] _value;

    internal ReferenceWrite(BinaryKey key, bool isDelete, ReadOnlySpan<byte> value)
    {
        Key = key;
        IsDelete = isDelete;
        _value = value.ToArray();
    }

    public BinaryKey Key { get; }

    public bool IsDelete { get; }

    public ReadOnlyMemory<byte> Value => _value;
}

public sealed class ReferenceTransactionConflictException : InvalidOperationException
{
    internal ReferenceTransactionConflictException(ulong startSequence, ulong conflictingSequence)
        : base($"Reference transaction at {startSequence} conflicts with commit {conflictingSequence}.")
    {
        StartSequence = startSequence;
        ConflictingSequence = conflictingSequence;
    }

    public ulong StartSequence { get; }

    public ulong ConflictingSequence { get; }
}

public sealed record ReferenceSnapshot(string Name, ulong Sequence);
