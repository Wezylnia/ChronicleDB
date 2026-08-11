using System.Text;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Snapshots;

/// <summary>
/// Thread-safe in-memory semantic catalog of persistent snapshot roots.
/// It contains no file I/O and is rebuilt from durable snapshot metadata on open.
/// </summary>
public sealed class SnapshotCatalog
{
    public const int MaxNameBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _gate = new();
    private readonly Dictionary<SnapshotId, SnapshotDefinition> _byId = [];
    private readonly Dictionary<string, SnapshotId> _byName = new(StringComparer.Ordinal);

    public SnapshotCatalog(
        CommitSequence retentionFloor,
        CommitSequence currentSequence,
        IEnumerable<SnapshotDefinition>? snapshots = null)
    {
        if (retentionFloor > currentSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionFloor),
                "The historical retention floor cannot be newer than current history.");
        }

        RetentionFloor = retentionFloor;
        if (snapshots is null)
        {
            return;
        }

        foreach (var snapshot in snapshots)
        {
            RegisterRecovered(snapshot, currentSequence);
        }
    }

    public CommitSequence RetentionFloor { get; private set; }

    public void AdvanceRetentionFloor(CommitSequence newFloor, CommitSequence currentSequence)
    {
        lock (_gate)
        {
            if (newFloor < RetentionFloor || newFloor > currentSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(newFloor));
            }
            RetentionFloor = newFloor;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    public SnapshotDefinition PrepareCreate(string name, CommitSequence boundary)
    {
        ValidateName(name);
        lock (_gate)
        {
            if (boundary < RetentionFloor)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(boundary),
                    "A persistent snapshot cannot be created below the retained historical floor.");
            }

            if (_byName.ContainsKey(name))
            {
                throw new InvalidOperationException($"A persistent snapshot named '{name}' already exists.");
            }

            return new SnapshotDefinition(
                SnapshotId.New(),
                name,
                boundary,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public void RegisterPersisted(SnapshotDefinition snapshot, CommitSequence currentSequence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ValidateDefinition(snapshot, currentSequence);
            AddDefinition(snapshot);
        }
    }

    public bool TryGet(SnapshotId snapshotId, out SnapshotDefinition? snapshot)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(snapshotId, out snapshot);
        }
    }

    public bool TryGet(string name, out SnapshotDefinition? snapshot)
    {
        ValidateName(name);
        lock (_gate)
        {
            if (_byName.TryGetValue(name, out var snapshotId)
                && _byId.TryGetValue(snapshotId, out snapshot))
            {
                return true;
            }

            snapshot = null;
            return false;
        }
    }

    public SnapshotDefinition GetRequired(SnapshotId snapshotId)
    {
        if (!snapshotId.IsValid)
        {
            throw new ArgumentException("A snapshot ID must be non-empty.", nameof(snapshotId));
        }

        lock (_gate)
        {
            return _byId.TryGetValue(snapshotId, out var snapshot)
                ? snapshot
                : throw new KeyNotFoundException($"Snapshot {snapshotId.Value} does not exist.");
        }
    }

    public SnapshotDefinition GetRequired(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            if (!_byName.TryGetValue(name, out var snapshotId)
                || !_byId.TryGetValue(snapshotId, out var snapshot))
            {
                throw new KeyNotFoundException($"Snapshot '{name}' does not exist.");
            }

            return snapshot;
        }
    }

    public SnapshotDefinition RemoveRequired(SnapshotId snapshotId)
    {
        if (!snapshotId.IsValid)
        {
            throw new ArgumentException("A snapshot ID must be non-empty.", nameof(snapshotId));
        }

        lock (_gate)
        {
            if (!_byId.Remove(snapshotId, out var snapshot))
            {
                throw new KeyNotFoundException($"Snapshot {snapshotId.Value} does not exist.");
            }

            _byName.Remove(snapshot.Name);
            return snapshot;
        }
    }

    public IReadOnlyList<SnapshotDefinition> List()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(snapshot => snapshot.Sequence.Value)
                .ThenBy(snapshot => snapshot.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private void RegisterRecovered(SnapshotDefinition snapshot, CommitSequence currentSequence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ValidateDefinition(snapshot, currentSequence);
            AddDefinition(snapshot);
        }
    }

    private void AddDefinition(SnapshotDefinition snapshot)
    {
        if (_byId.ContainsKey(snapshot.SnapshotId))
        {
            throw new InvalidOperationException($"Snapshot ID {snapshot.SnapshotId.Value} is duplicated.");
        }

        if (_byName.ContainsKey(snapshot.Name))
        {
            throw new InvalidOperationException($"Snapshot name '{snapshot.Name}' is duplicated.");
        }

        _byId.Add(snapshot.SnapshotId, snapshot);
        _byName.Add(snapshot.Name, snapshot.SnapshotId);
    }

    private static void ValidateDefinition(SnapshotDefinition snapshot, CommitSequence currentSequence)
    {
        if (!snapshot.SnapshotId.IsValid)
        {
            throw new ArgumentException("A snapshot definition requires a non-empty ID.", nameof(snapshot));
        }

        ValidateName(snapshot.Name);
        if (snapshot.Sequence > currentSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "The snapshot boundary lies beyond committed history.");
        }

        if (snapshot.CreatedUnixMilliseconds < 0
            || snapshot.CreatedUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Snapshot creation time is invalid.");
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Snapshot names may not contain leading or trailing whitespace.", nameof(name));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(name);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Snapshot names must be valid UTF-8 text.", nameof(name), exception);
        }

        if (byteCount > MaxNameBytes)
        {
            throw new ArgumentException(
                $"Snapshot names may use at most {MaxNameBytes} UTF-8 bytes.",
                nameof(name));
        }
    }
}
