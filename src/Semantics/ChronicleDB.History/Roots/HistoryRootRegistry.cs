using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Roots;

/// <summary>
/// Thread-safe semantic registry for historical roots.
/// It is deliberately independent of file I/O so storage implementations can
/// publish the same lifecycle transitions through different durable protocols.
/// </summary>
public sealed class HistoryRootRegistry
{
    private static readonly long MaximumUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private readonly object _gate = new();
    private readonly Dictionary<HistoryRootId, HistoryRoot> _roots = [];
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
                .OrderBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .ToArray();
        }
    }

    public IReadOnlyList<HistoryRoot> ListActive(HistoryId? historyId = null)
    {
        lock (_gate)
        {
            return _roots.Values
                .Where(root => root.IsRetaining)
                .Where(root => historyId is null || root.HistoryId == historyId.Value)
                .OrderBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .ToArray();
        }
    }

    /// <summary>
    /// Returns the conservative oldest sequence that must remain available for
    /// the requested history. A history with no roots has no protected range.
    /// </summary>
    public CommitSequence? GetRetentionFloor(HistoryId historyId)
    {
        if (!historyId.IsValid)
        {
            throw new ArgumentException("A history ID must be non-empty.", nameof(historyId));
        }

        lock (_gate)
        {
            CommitSequence? floor = historyId == _baselineHistoryId ? _baselineFloor : null;
            foreach (var root in _roots.Values)
            {
                if (root.IsRetaining && root.HistoryId == historyId)
                {
                    floor = floor is null || root.Boundary < floor.Value
                        ? root.Boundary
                        : floor;
                }
            }

            return floor;
        }
    }

    public IReadOnlyList<HistoryRetentionRequirement> GetRetentionRequirements(HistoryId? historyId = null)
    {
        lock (_gate)
        {
            return _roots.Values
                .Where(root => root.IsRetaining)
                .Where(root => historyId is null || root.HistoryId == historyId.Value)
                .OrderBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .Select(root => new HistoryRetentionRequirement(
                    root.RootId,
                    root.Kind,
                    root.HistoryId,
                    root.Boundary,
                    root.State))
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

        if (root.Kind == HistoryRootKind.BranchBase
            && (!root.ParentHistoryId.IsValid || root.ParentHistoryId == root.HistoryId))
        {
            throw new ArgumentException(
                "A branch base must identify a distinct valid parent history.",
                nameof(root));
        }

        if (root.OwnerDatabaseId == Guid.Empty || !root.HistoryId.IsValid)
        {
            throw new ArgumentException(
                "A history root requires a valid owner database and history identity.",
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
