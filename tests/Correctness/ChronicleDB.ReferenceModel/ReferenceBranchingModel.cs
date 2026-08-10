using ChronicleDB.Core.Keys;

namespace ChronicleDB.ReferenceModel;

/// <summary>
/// Deliberately simple global-lock oracle for branch semantics. Each history owns
/// only local version chains and has one immutable parent boundary; there is no
/// physical sharing, WAL, or storage optimization in the reference model.
/// </summary>
public sealed class ReferenceBranchingModel
{
    private readonly object _gate = new();
    private readonly Dictionary<string, History> _histories = new(StringComparer.Ordinal)
    {
        ["main"] = new History("main", null, 0),
    };

    public ulong CurrentSequence(string history)
    {
        lock (_gate)
        {
            return Get(history).CurrentSequence;
        }
    }

    public void CreateBranch(string parent, ulong parentBoundary, string name)
    {
        lock (_gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_histories.ContainsKey(name))
            {
                throw new InvalidOperationException($"History '{name}' already exists.");
            }

            var parentHistory = Get(parent);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                parentBoundary,
                parentHistory.CurrentSequence,
                nameof(parentBoundary));

            _histories.Add(name, new History(name, parentHistory, parentBoundary));
        }
    }

    public ReferenceBranchTransaction Begin(string history)
    {
        lock (_gate)
        {
            var target = Get(history);
            return new ReferenceBranchTransaction(this, history, target.CurrentSequence);
        }
    }

    public bool TryRead(string history, ulong boundary, ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            var target = Get(history);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                boundary,
                target.CurrentSequence,
                nameof(boundary));
            return TryReadLocked(target, boundary, new BinaryKey(key), out value);
        }
    }

    internal ulong Commit(
        string history,
        ulong startSequence,
        IReadOnlyDictionary<BinaryKey, ReferenceBranchWrite> writes)
    {
        lock (_gate)
        {
            var target = Get(history);
            foreach (var write in writes.Values)
            {
                if (target.LocalVersions.TryGetValue(write.Key, out var chain)
                    && chain.Count != 0
                    && chain[^1].Sequence > startSequence)
                {
                    throw new ReferenceTransactionConflictException(startSequence, chain[^1].Sequence);
                }
            }

            target.CurrentSequence = checked(target.CurrentSequence + 1);
            foreach (var write in writes.Values)
            {
                if (!target.LocalVersions.TryGetValue(write.Key, out var chain))
                {
                    chain = [];
                    target.LocalVersions.Add(write.Key, chain);
                }
                chain.Add(new Version(target.CurrentSequence, write.IsDelete, write.Value.ToArray()));
            }
            return target.CurrentSequence;
        }
    }

    internal bool TryReadAt(string history, ulong boundary, ReadOnlySpan<byte> key, out byte[] value)
    {
        lock (_gate)
        {
            return TryReadLocked(Get(history), boundary, new BinaryKey(key), out value);
        }
    }

    private static bool TryReadLocked(History history, ulong boundary, BinaryKey key, out byte[] value)
    {
        if (history.LocalVersions.TryGetValue(key, out var chain))
        {
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var version = chain[index];
                if (version.Sequence > boundary)
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
        }

        if (history.Parent is not null)
        {
            return TryReadLocked(history.Parent, history.ParentBoundary, key, out value);
        }

        value = [];
        return false;
    }

    private History Get(string name)
        => _histories.TryGetValue(name, out var history)
            ? history
            : throw new KeyNotFoundException($"History '{name}' does not exist.");

    private sealed class History(string name, History? parent, ulong parentBoundary)
    {
        public string Name { get; } = name;
        public History? Parent { get; } = parent;
        public ulong ParentBoundary { get; } = parentBoundary;
        public ulong CurrentSequence { get; set; }
        public Dictionary<BinaryKey, List<Version>> LocalVersions { get; } = [];
    }

    private sealed record Version(ulong Sequence, bool IsDelete, byte[] Value);
}

public sealed class ReferenceBranchTransaction : IDisposable
{
    private readonly ReferenceBranchingModel _model;
    private readonly string _history;
    private readonly Dictionary<BinaryKey, ReferenceBranchWrite> _writes = [];
    private bool _completed;

    internal ReferenceBranchTransaction(ReferenceBranchingModel model, string history, ulong startSequence)
    {
        _model = model;
        _history = history;
        StartSequence = startSequence;
    }

    public ulong StartSequence { get; }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ThrowIfCompleted();
        var binaryKey = new BinaryKey(key);
        _writes[binaryKey] = new ReferenceBranchWrite(binaryKey, false, value);
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        ThrowIfCompleted();
        var binaryKey = new BinaryKey(key);
        _writes[binaryKey] = new ReferenceBranchWrite(binaryKey, true, []);
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
        return _model.TryReadAt(_history, StartSequence, key, out value);
    }

    public ulong Commit()
    {
        ThrowIfCompleted();
        try
        {
            return _model.Commit(_history, StartSequence, _writes);
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
            throw new InvalidOperationException("Reference branch transaction is complete.");
        }
    }
}

public sealed class ReferenceBranchWrite
{
    private readonly byte[] _value;

    internal ReferenceBranchWrite(BinaryKey key, bool isDelete, ReadOnlySpan<byte> value)
    {
        Key = key;
        IsDelete = isDelete;
        _value = value.ToArray();
    }

    public BinaryKey Key { get; }
    public bool IsDelete { get; }
    public ReadOnlyMemory<byte> Value => _value;
}
