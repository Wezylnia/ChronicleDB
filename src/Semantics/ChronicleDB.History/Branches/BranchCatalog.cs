using System.Text;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Branches;

/// <summary>
/// Thread-safe semantic catalog of active branches. It owns identity/name uniqueness
/// and local sequence publication, but performs no file I/O.
/// </summary>
public sealed class BranchCatalog
{
    public const int MaxNameBytes = 1024;
    public const int MaximumDepth = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly long MaximumUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private readonly object _gate = new();
    private readonly Dictionary<BranchId, BranchDefinition> _byId = [];
    private readonly Dictionary<string, BranchId> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<HistoryId, BranchId> _byHistory = [];

    public BranchCatalog(IEnumerable<BranchDefinition>? branches = null)
    {
        if (branches is null)
        {
            return;
        }

        foreach (var branch in branches)
        {
            RegisterActive(branch);
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

    public static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Branch names may not contain leading or trailing whitespace.", nameof(name));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(name);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Branch names must be valid UTF-8 text.", nameof(name), exception);
        }

        if (byteCount > MaxNameBytes)
        {
            throw new ArgumentException(
                $"Branch names may use at most {MaxNameBytes} UTF-8 bytes.",
                nameof(name));
        }
    }

    public void EnsureNameAvailable(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            if (_byName.ContainsKey(name))
            {
                throw new InvalidOperationException($"A branch named '{name}' already exists.");
            }
        }
    }

    public void RegisterActive(BranchDefinition branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ValidateDefinition(branch);
        lock (_gate)
        {
            if (_byId.ContainsKey(branch.BranchId))
            {
                throw new InvalidOperationException($"Branch ID {branch.BranchId.Value} is duplicated.");
            }

            if (_byHistory.ContainsKey(branch.HistoryId))
            {
                throw new InvalidOperationException($"History ID {branch.HistoryId.Value} is owned by multiple branches.");
            }

            if (_byName.ContainsKey(branch.Name))
            {
                throw new InvalidOperationException($"Branch name '{branch.Name}' is duplicated.");
            }

            _byId.Add(branch.BranchId, branch);
            _byHistory.Add(branch.HistoryId, branch.BranchId);
            _byName.Add(branch.Name, branch.BranchId);
        }
    }

    public BranchDefinition PublishCommit(
        BranchId branchId,
        CommitSequence expectedPrevious,
        CommitSequence committedSequence)
    {
        lock (_gate)
        {
            var current = GetRequiredLocked(branchId);
            if (current.LocalCurrentSequence != expectedPrevious)
            {
                throw new InvalidOperationException(
                    $"Branch {branchId.Value} expected sequence {expectedPrevious.Value} but is at " +
                    $"{current.LocalCurrentSequence.Value}.");
            }

            CommitSequence expectedNext;
            try
            {
                expectedNext = expectedPrevious.Next();
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("The branch commit-sequence space is exhausted.", exception);
            }

            if (committedSequence != expectedNext)
            {
                throw new InvalidOperationException(
                    $"Branch commit publication must advance exactly to {expectedNext.Value}.");
            }

            var updated = current.WithCurrentSequence(committedSequence);
            _byId[branchId] = updated;
            return updated;
        }
    }

    public bool TryGet(BranchId branchId, out BranchDefinition? branch)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(branchId, out branch);
        }
    }

    public bool TryGet(string name, out BranchDefinition? branch)
    {
        ValidateName(name);
        lock (_gate)
        {
            if (_byName.TryGetValue(name, out var branchId)
                && _byId.TryGetValue(branchId, out branch))
            {
                return true;
            }

            branch = null;
            return false;
        }
    }

    public bool TryGetByHistory(HistoryId historyId, out BranchDefinition? branch)
    {
        if (!historyId.IsValid)
        {
            branch = null;
            return false;
        }

        lock (_gate)
        {
            if (_byHistory.TryGetValue(historyId, out var branchId)
                && _byId.TryGetValue(branchId, out branch))
            {
                return true;
            }

            branch = null;
            return false;
        }
    }

    public BranchDefinition GetRequired(BranchId branchId)
    {
        if (!branchId.IsValid)
        {
            throw new ArgumentException("A branch ID must be non-empty.", nameof(branchId));
        }

        lock (_gate)
        {
            return GetRequiredLocked(branchId);
        }
    }

    public IReadOnlyList<BranchDefinition> List()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(branch => branch.Depth)
                .ThenBy(branch => branch.Name, StringComparer.Ordinal)
                .ThenBy(branch => branch.BranchId.Value)
                .ToArray();
        }
    }

    private BranchDefinition GetRequiredLocked(BranchId branchId)
        => _byId.TryGetValue(branchId, out var branch)
            ? branch
            : throw new KeyNotFoundException($"Branch {branchId.Value} does not exist.");

    private static void ValidateDefinition(BranchDefinition branch)
    {
        if (!branch.BranchId.IsValid
            || branch.OwnerDatabaseId == Guid.Empty
            || !branch.HistoryId.IsValid
            || !branch.ParentHistoryId.IsValid
            || branch.HistoryId == branch.ParentHistoryId
            || !branch.BaseRootId.IsValid
            || branch.LocalStorageId == Guid.Empty)
        {
            throw new ArgumentException("Branch identity metadata is invalid.", nameof(branch));
        }

        ValidateName(branch.Name);
        if (branch.State != BranchLifecycleState.Active)
        {
            throw new ArgumentException("Only active branches may enter the semantic catalog.", nameof(branch));
        }

        if (branch.Depth is < 1 or > MaximumDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(branch), "Branch depth is outside the supported range.");
        }

        if (branch.CreatedUnixMilliseconds < 0 || branch.CreatedUnixMilliseconds > MaximumUnixMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(branch), "Branch creation time is invalid.");
        }
    }
}
