using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Roots;

/// <summary>
/// Thread-safe semantic registry for historical roots and conservative per-history
/// retention floors. It is deliberately independent of file I/O so persistence
/// implementations can publish the same lifecycle transitions through different
/// durable protocols.
/// </summary>
public sealed class HistoryRootRegistry
{
    private static readonly long MaximumUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private readonly object _gate = new();
    private readonly Dictionary<HistoryRootId, HistoryRoot> _roots = [];
    private readonly Dictionary<HistoryId, CommitSequence> _historyFloors = [];
    private readonly HistoryId _baselineHistoryId;
    private readonly CommitSequence _baselineFloor;

    public HistoryRootRegistry(
        HistoryId baselineHistoryId,
        CommitSequence baselineFloor,
        IEnumerable<HistoryRoot>? roots = null)
    {
        if (!baselineHistoryId.IsValid)
        {
            throw new ArgumentException("A baseline history must have a valid identity.", nameof(baselineHistoryId));
        }

        _baselineHistoryId = baselineHistoryId;
        _baselineFloor = baselineFloor;
        _historyFloors.Add(baselineHistoryId, baselineFloor);

        if (roots is null)
        {
            return;
        }

        foreach (var root in roots)
        {
            RegisterRecovered(root);
        }
    }

    public HistoryId BaselineHistoryId => _baselineHistoryId;

    public CommitSequence BaselineFloor => _baselineFloor;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _roots.Count(root => root.Value.State is not HistoryRootState.Deleted);
            }
        }
    }

    /// <summary>
    /// Registers an independently evolving history domain and the oldest local
    /// boundary that can currently be reconstructed for it. Re-registering the
    /// same domain with a different floor is an invariant violation.
    /// </summary>
    public void RegisterHistory(HistoryId historyId, CommitSequence retentionFloor)
    {
        if (!historyId.IsValid)
        {
            throw new ArgumentException("A history ID must be non-empty.", nameof(historyId));
        }

        lock (_gate)
        {
            if (_historyFloors.TryGetValue(historyId, out var existing))
            {
                if (existing != retentionFloor)
                {
                    throw new InvalidOperationException(
                        $"History {historyId.Value} is already registered with retention floor {existing.Value}.");
                }

                return;
            }

            _historyFloors.Add(historyId, retentionFloor);
        }
    }

    public bool IsHistoryRegistered(HistoryId historyId)
    {
        if (!historyId.IsValid)
        {
            return false;
        }

        lock (_gate)
        {
            return _historyFloors.ContainsKey(historyId);
        }
    }

    /// <summary>
    /// Registers a durable creation intent. Creating roots retain history until
    /// activation or deterministic cleanup completes.
    /// </summary>
    public void RegisterCreating(HistoryRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateRoot(root, HistoryRootState.Creating);
        lock (_gate)
        {
            AddNewRoot(root);
        }
    }

    public void RegisterActive(HistoryRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateRoot(root, HistoryRootState.Active);
        lock (_gate)
        {
            AddNewRoot(root);
        }
    }

    public void Activate(HistoryRootId rootId)
    {
        lock (_gate)
        {
            var root = GetRequiredLocked(rootId);
            EnsureState(root, HistoryRootState.Creating);
            _roots[rootId] = root.WithState(HistoryRootState.Active);
        }
    }

    public void BeginDelete(HistoryRootId rootId)
    {
        lock (_gate)
        {
            var root = GetRequiredLocked(rootId);
            EnsureState(root, HistoryRootState.Active);
            _roots[rootId] = root.WithState(HistoryRootState.Deleting);
        }
    }

    public void CompleteDelete(HistoryRootId rootId)
    {
        lock (_gate)
        {
            var root = GetRequiredLocked(rootId);
            EnsureState(root, HistoryRootState.Deleting);
            _roots[rootId] = root.WithState(HistoryRootState.Deleted);
        }
    }

    /// <summary>
    /// Cancels a deletion intent when its durable delete record was not
    /// published. The root remains protected and active.
    /// </summary>
    public void CancelDelete(HistoryRootId rootId)
    {
        lock (_gate)
        {
            var root = GetRequiredLocked(rootId);
            EnsureState(root, HistoryRootState.Deleting);
            _roots[rootId] = root.WithState(HistoryRootState.Active);
        }
    }

    public bool TryGet(HistoryRootId rootId, out HistoryRoot? root)
    {
        lock (_gate)
        {
            return _roots.TryGetValue(rootId, out root);
        }
    }

    public IReadOnlyList<HistoryRoot> ListAll()
    {
        lock (_gate)
        {
            return _roots.Values
                .OrderBy(root => root.ProtectedHistoryId.Value)
                .ThenBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .ToArray();
        }
    }

    /// <summary>
    /// Lists all roots that still retain history. The optional history filter is
    /// applied to the protected history rather than the root owner's history.
    /// </summary>
    public IReadOnlyList<HistoryRoot> ListActive(HistoryId? protectedHistoryId = null)
    {
        lock (_gate)
        {
            return _roots.Values
                .Where(root => root.IsRetaining)
                .Where(root => protectedHistoryId is null || root.ProtectedHistoryId == protectedHistoryId.Value)
                .OrderBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .ToArray();
        }
    }

    /// <summary>
    /// Returns the conservative oldest sequence that must remain reconstructable
    /// for a history. Registered history floors are always honored, then retaining
    /// roots may move the protected boundary further into the past.
    /// </summary>
    public CommitSequence? GetRetentionFloor(HistoryId historyId)
    {
        if (!historyId.IsValid)
        {
            throw new ArgumentException("A history ID must be non-empty.", nameof(historyId));
        }

        lock (_gate)
        {
            CommitSequence? floor = _historyFloors.TryGetValue(historyId, out var baseline)
                ? baseline
                : null;
            foreach (var root in _roots.Values)
            {
                if (root.IsRetaining && root.ProtectedHistoryId == historyId)
                {
                    floor = floor is null || root.Boundary < floor.Value
                        ? root.Boundary
                        : floor;
                }
            }

            return floor;
        }
    }

    public IReadOnlyList<HistoryRetentionRequirement> GetRetentionRequirements(
        HistoryId? protectedHistoryId = null)
    {
        lock (_gate)
        {
            return _roots.Values
                .Where(root => root.IsRetaining)
                .Where(root => protectedHistoryId is null || root.ProtectedHistoryId == protectedHistoryId.Value)
                .OrderBy(root => root.ProtectedHistoryId.Value)
                .ThenBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .Select(root => new HistoryRetentionRequirement(
                    root.RootId,
                    root.Kind,
                    root.HistoryId,
                    root.ProtectedHistoryId,
                    root.Boundary,
                    root.State))
                .ToArray();
        }
    }

    /// <summary>
    /// Returns active branch-base roots whose child history depends on the
    /// requested parent history. This becomes the ancestry/GC dependency query.
    /// </summary>
    public IReadOnlyList<HistoryRoot> GetBranchDependents(HistoryId parentHistoryId)
    {
        if (!parentHistoryId.IsValid)
        {
            throw new ArgumentException("A history ID must be non-empty.", nameof(parentHistoryId));
        }

        lock (_gate)
        {
            return _roots.Values
                .Where(root => root.IsRetaining
                    && root.Kind == HistoryRootKind.BranchBase
                    && root.ParentHistoryId == parentHistoryId)
                .OrderBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .ToArray();
        }
    }

    private void RegisterRecovered(HistoryRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateRoot(root, root.State);
        lock (_gate)
        {
            AddNewRoot(root);
        }
    }

    private void AddNewRoot(HistoryRoot root)
    {
        if (!_roots.TryAdd(root.RootId, root))
        {
            throw new InvalidOperationException($"History root {root.RootId.Value} is duplicated.");
        }
    }

    private HistoryRoot GetRequiredLocked(HistoryRootId rootId)
    {
        if (!rootId.IsValid)
        {
            throw new ArgumentException("A history root ID must be non-empty.", nameof(rootId));
        }

        return _roots.TryGetValue(rootId, out var root)
            ? root
            : throw new KeyNotFoundException($"History root {rootId.Value} does not exist.");
    }

    private static void EnsureState(HistoryRoot root, HistoryRootState expected)
    {
        if (root.State != expected)
        {
            throw new InvalidOperationException(
                $"History root {root.RootId.Value} is {root.State}, expected {expected}.");
        }
    }

    private static void ValidateRoot(HistoryRoot root, HistoryRootState expectedState)
    {
        if (!root.RootId.IsValid)
        {
            throw new ArgumentException("A history root requires a non-empty identity.", nameof(root));
        }

        if (!Enum.IsDefined(root.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(root), "The history root kind is invalid.");
        }

        if (root.OwnerDatabaseId == Guid.Empty || !root.HistoryId.IsValid)
        {
            throw new ArgumentException(
                "A history root requires a valid owner database and history identity.",
                nameof(root));
        }

        if (root.Kind == HistoryRootKind.BranchBase)
        {
            if (!root.ParentHistoryId.IsValid || root.ParentHistoryId == root.HistoryId)
            {
                throw new ArgumentException(
                    "A branch base must identify a distinct valid parent history.",
                    nameof(root));
            }
        }
        else if (root.ParentHistoryId.IsValid)
        {
            throw new ArgumentException(
                "Only branch-base roots may identify a parent history.",
                nameof(root));
        }

        if (root.CreatedUnixMilliseconds < 0 || root.CreatedUnixMilliseconds > MaximumUnixMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(root), "History root creation time is invalid.");
        }

        if (root.State != expectedState)
        {
            throw new ArgumentException(
                $"The root descriptor state must be {expectedState} for this operation.",
                nameof(root));
        }
    }
}
