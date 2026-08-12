using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Indexing;
using ChronicleDB.Mvcc.Versions;
using ChronicleDB.Mvcc.Visibility;
using ChronicleDB.Storage.Files;
using ChronicleDB.Transactions.Writes;

namespace ChronicleDB.Transactions.Mvcc;

/// <summary>
/// Managed version-chain store. The index only locates chain heads; this type remains
/// authoritative for MVCC visibility and immutable committed history. A writer lock
/// makes multi-key publication atomic to readers while allowing parallel read traversal.
/// </summary>
public sealed class CommittedVersionStore : IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly IVersionIndex _index;
    private readonly Dictionary<VersionHandle, CommittedVersionRecord> _versions = [];
    private ulong _nextHandle = 1;
    private int _currentKeyCount;
    private int _maximumChainLength;

    public CommittedVersionStore(IVersionIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public int VersionCount
    {
        get
        {
            _gate.EnterReadLock();
            try
            {
                return _versions.Count;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public int CurrentKeyCount
    {
        get
        {
            _gate.EnterReadLock();
            try
            {
                return _currentKeyCount;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public bool TryRead(BinaryKey key, CommitSequence visibilityBoundary, out byte[] value)
    {
        var resolution = Resolve(key, visibilityBoundary);
        if (resolution.Kind == CommittedVersionResolutionKind.Value)
        {
            value = resolution.Value;
            return true;
        }

        value = [];
        return false;
    }

    /// <summary>
    /// Reads the newest committed state under the same read lock that resolves the
    /// index head. Current-state callers must not sample a commit sequence outside
    /// this lock because concurrent GC may legitimately reclaim older unpinned
    /// versions after a newer commit becomes current.
    /// </summary>
    public bool TryReadLatest(BinaryKey key, out byte[] value)
    {
        var resolution = ResolveLatest(key);
        if (resolution.Kind == CommittedVersionResolutionKind.Value)
        {
            value = resolution.Value;
            return true;
        }

        value = [];
        return false;
    }

    /// <summary>
    /// Resolves the newest local committed state while preserving tombstone versus
    /// missing semantics for branch fallback.
    /// </summary>
    public CommittedVersionResolution ResolveLatest(BinaryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _gate.EnterReadLock();
        try
        {
            if (!_index.TryGet(key, out var current))
            {
                return CommittedVersionResolution.Missing;
            }

            var version = GetVersion(current);
            return version.Metadata.IsTombstone
                ? CommittedVersionResolution.Deleted
                : CommittedVersionResolution.Present(version.Value.ToArray());
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Resolves a key while preserving the distinction between "no local version"
    /// and a visible tombstone. Branch fallback requires this distinction: a tombstone
    /// intentionally shadows inherited parent state, while no local version falls back.
    /// </summary>
    public CommittedVersionResolution Resolve(BinaryKey key, CommitSequence visibilityBoundary)
    {
        ArgumentNullException.ThrowIfNull(key);
        _gate.EnterReadLock();
        try
        {
            if (!_index.TryGet(key, out var current))
            {
                return CommittedVersionResolution.Missing;
            }

            while (current.IsValid)
            {
                var version = GetVersion(current);
                if (VersionVisibility.IsVisible(version.Metadata, visibilityBoundary))
                {
                    return version.Metadata.IsTombstone
                        ? CommittedVersionResolution.Deleted
                        : CommittedVersionResolution.Present(version.Value.ToArray());
                }

                current = version.Previous;
            }

            return CommittedVersionResolution.Missing;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool TryGetLatestCommitSequence(BinaryKey key, out CommitSequence commitSequence)
    {
        ArgumentNullException.ThrowIfNull(key);
        _gate.EnterReadLock();
        try
        {
            if (!_index.TryGet(key, out var head))
            {
                commitSequence = CommitSequence.Initial;
                return false;
            }

            commitSequence = GetVersion(head).Metadata.CommitSequence;
            return true;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void ValidatePublicationCapacity(IReadOnlyList<TransactionWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        _gate.EnterReadLock();
        try
        {
            var keys = new HashSet<BinaryKey>();
            foreach (var write in writes)
            {
                ArgumentNullException.ThrowIfNull(write);
                keys.Add(write.Key);
            }

            ValidateCapacityCore(keys);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void PublishCommitted(
        TransactionId creatorTransaction,
        CommitSequence commitSequence,
        IReadOnlyList<TransactionWrite> writes)
    {
        if (!creatorTransaction.IsValid)
        {
            throw new ArgumentException("A committed version requires a valid creator transaction.", nameof(creatorTransaction));
        }

        ArgumentNullException.ThrowIfNull(writes);
        EnsureCommittedSequence(commitSequence);

        _gate.EnterWriteLock();
        try
        {
            foreach (var write in writes)
            {
                ArgumentNullException.ThrowIfNull(write);
                PublishMutation(
                    creatorTransaction,
                    commitSequence,
                    write.Key,
                    write.IsDelete,
                    write.Value.Span);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void ValidateReplayCapacity(IReadOnlyList<StorageMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        _gate.EnterReadLock();
        try
        {
            var finalKeys = new HashSet<BinaryKey>();
            foreach (var mutation in mutations)
            {
                ArgumentNullException.ThrowIfNull(mutation);
                finalKeys.Add(mutation.Key);
            }

            ValidateCapacityCore(finalKeys);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void ReplayCommitted(
        TransactionId creatorTransaction,
        CommitSequence commitSequence,
        IReadOnlyList<StorageMutation> mutations)
    {
        if (!creatorTransaction.IsValid)
        {
            throw new ArgumentException("A replayed version requires a valid creator transaction.", nameof(creatorTransaction));
        }

        ArgumentNullException.ThrowIfNull(mutations);
        EnsureCommittedSequence(commitSequence);

        _gate.EnterWriteLock();
        try
        {
            // A transaction contributes one logical version per key: its final local write.
            var finalMutations = new Dictionary<BinaryKey, StorageMutation>();
            foreach (var mutation in mutations)
            {
                ArgumentNullException.ThrowIfNull(mutation);
                finalMutations[mutation.Key] = mutation;
            }

            foreach (var mutation in finalMutations.Values)
            {
                PublishMutation(
                    creatorTransaction,
                    commitSequence,
                    mutation.Key,
                    mutation.IsDelete,
                    mutation.Value.Span);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Produces the exact retained projection used by v0.9 checkpoints. All versions
    /// at/above the generic time-travel floor are preserved, while older history is
    /// retained only at explicit root boundaries. The newest version is always kept.
    /// </summary>
    public IReadOnlyList<CommittedVersionSnapshot> CreateRetentionProjection(
        CommitSequence retentionFloor,
        IReadOnlyCollection<CommitSequence>? pinnedBoundaries = null)
    {
        _gate.EnterReadLock();
        try
        {
            var keep = SelectRetainedHandles(retentionFloor, pinnedBoundaries);
            return _versions.Values
                .Where(version => keep.Contains(version.Handle))
                .OrderBy(version => version.Metadata.CommitSequence.Value)
                .ThenBy(version => version.Key, BinaryKeyLexicographicComparer.Instance)
                .Select(ToSnapshot)
                .ToArray();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes logically unreachable committed versions after an equivalent durable
    /// checkpoint has been published. Readers continue to see the same values for the
    /// generic retained range and every explicitly pinned historical boundary.
    /// </summary>
    public HistoryCompactionResult CompactHistory(
        CommitSequence retentionFloor,
        IReadOnlyCollection<CommitSequence>? pinnedBoundaries = null)
    {
        _gate.EnterWriteLock();
        try
        {
            return CompactToHandles(SelectRetainedHandles(retentionFloor, pinnedBoundaries));
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies an exact retained projection that was computed by a higher-level
    /// research/protocol layer. The projection must be a byte-for-byte subset of
    /// this store's current immutable history and must retain every key's latest
    /// version so current-state semantics and index heads cannot change.
    /// </summary>
    public HistoryCompactionResult CompactHistoryToProjection(
        IReadOnlyCollection<CommittedVersionSnapshot> retainedProjection)
    {
        ArgumentNullException.ThrowIfNull(retainedProjection);

        _gate.EnterWriteLock();
        try
        {
            var currentByIdentity = _versions.Values.ToDictionary(
                version => new VersionIdentity(
                    version.CreatorTransaction,
                    version.Metadata.CommitSequence,
                    version.Key));
            var requested = new Dictionary<VersionIdentity, CommittedVersionSnapshot>();
            foreach (var snapshot in retainedProjection)
            {
                ArgumentNullException.ThrowIfNull(snapshot);
                var identity = new VersionIdentity(snapshot.TransactionId, snapshot.CommitSequence, snapshot.Key);
                if (!requested.TryAdd(identity, snapshot))
                {
                    throw new ArgumentException(
                        "An exact retained projection cannot contain one logical version twice.",
                        nameof(retainedProjection));
                }

                if (!currentByIdentity.TryGetValue(identity, out var current)
                    || current.Metadata.IsTombstone != snapshot.IsDelete
                    || !current.Value.AsSpan().SequenceEqual(snapshot.Value.Span))
                {
                    throw new ArgumentException(
                        "The exact retained projection contains a version that is not identical to current history.",
                        nameof(retainedProjection));
                }
            }

            foreach (var group in _versions.Values.GroupBy(version => version.Key))
            {
                var latest = group.MaxBy(version => version.Metadata.CommitSequence.Value)!;
                var latestIdentity = new VersionIdentity(
                    latest.CreatorTransaction,
                    latest.Metadata.CommitSequence,
                    latest.Key);
                if (!requested.ContainsKey(latestIdentity))
                {
                    throw new ArgumentException(
                        "An exact retained projection must preserve every key's latest logical version.",
                        nameof(retainedProjection));
                }
            }

            var keep = currentByIdentity
                .Where(pair => requested.ContainsKey(pair.Key))
                .Select(pair => pair.Value.Handle)
                .ToHashSet();
            return CompactToHandles(keep);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public IReadOnlyList<CommittedVersionSnapshot> SnapshotHistory()
    {
        _gate.EnterReadLock();
        try
        {
            return _versions.Values
                .OrderBy(version => version.Metadata.CommitSequence.Value)
                .ThenBy(version => version.Key, BinaryKeyLexicographicComparer.Instance)
                .Select(ToSnapshot)
                .ToArray();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private HistoryCompactionResult CompactToHandles(HashSet<VersionHandle> keep)
    {
        var before = _versions.Count;
        var all = _versions.Values.ToArray();
        foreach (var version in all)
        {
            if (!keep.Contains(version.Handle))
            {
                _versions.Remove(version.Handle);
            }
        }

        _maximumChainLength = 0;
        foreach (var group in _versions.Values.GroupBy(version => version.Key).ToArray())
        {
            var ordered = group.OrderBy(version => version.Metadata.CommitSequence.Value).ToArray();
            var previous = VersionHandle.Invalid;
            var chainLength = 0;
            foreach (var version in ordered)
            {
                chainLength = checked(chainLength + 1);
                _versions[version.Handle] = version with
                {
                    Previous = previous,
                    ChainLength = chainLength,
                };
                previous = version.Handle;
            }

            if (ordered.Length != 0)
            {
                _index.Publish(group.Key, ordered[^1].Handle);
                _maximumChainLength = Math.Max(_maximumChainLength, chainLength);
            }
        }

        return new HistoryCompactionResult(before - _versions.Count, _versions.Count);
    }

    private HashSet<VersionHandle> SelectRetainedHandles(
        CommitSequence retentionFloor,
        IReadOnlyCollection<CommitSequence>? pinnedBoundaries)
    {
        var boundaries = new HashSet<CommitSequence> { retentionFloor };
        if (pinnedBoundaries is not null)
        {
            foreach (var boundary in pinnedBoundaries)
            {
                boundaries.Add(boundary);
            }
        }

        var keep = new HashSet<VersionHandle>();
        foreach (var group in _versions.Values.GroupBy(version => version.Key))
        {
            var ordered = group.OrderBy(version => version.Metadata.CommitSequence.Value).ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var firstGenericIndex = LowerBound(ordered, retentionFloor);
            for (var index = firstGenericIndex; index < ordered.Length; index++)
            {
                keep.Add(ordered[index].Handle);
            }

            foreach (var boundary in boundaries)
            {
                var visibleIndex = UpperBound(ordered, boundary) - 1;
                if (visibleIndex >= 0)
                {
                    keep.Add(ordered[visibleIndex].Handle);
                }
            }

            keep.Add(ordered[^1].Handle);
        }
        return keep;
    }

    private static int LowerBound(CommittedVersionRecord[] ordered, CommitSequence boundary)
    {
        var low = 0;
        var high = ordered.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (ordered[middle].Metadata.CommitSequence < boundary)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int UpperBound(CommittedVersionRecord[] ordered, CommitSequence boundary)
    {
        var low = 0;
        var high = ordered.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (ordered[middle].Metadata.CommitSequence <= boundary)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static CommittedVersionSnapshot ToSnapshot(CommittedVersionRecord version)
        => new(
            version.CreatorTransaction,
            version.Metadata.CommitSequence,
            version.Key,
            version.Metadata.IsTombstone,
            version.Value.ToArray());

    public CommittedVersionStoreStatistics GetStatistics()
    {
        _gate.EnterReadLock();
        try
        {
            var chainCount = _index.Count;
            var averageChainLength = chainCount == 0 ? 0d : (double)_versions.Count / chainCount;
            return new CommittedVersionStoreStatistics(
                VersionCount: _versions.Count,
                CurrentKeyCount: _currentKeyCount,
                ChainCount: chainCount,
                AverageChainLength: averageChainLength,
                MaximumChainLength: _maximumChainLength,
                Index: _index.GetStatistics());
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private void ValidateCapacityCore(HashSet<BinaryKey> keys)
    {
        var requiredHandles = checked((ulong)keys.Count);
        if (requiredHandles > ulong.MaxValue - _nextHandle)
        {
            throw new InvalidOperationException("The managed version-handle space cannot fit the complete publication.");
        }

        foreach (var key in keys)
        {
            if (_index.TryGet(key, out var currentHead)
                && GetVersion(currentHead).ChainLength == int.MaxValue)
            {
                throw new InvalidOperationException(
                    "The version chain length limit is exhausted for a publication key.");
            }
        }
    }

    private void PublishMutation(
        TransactionId creatorTransaction,
        CommitSequence commitSequence,
        BinaryKey key,
        bool isDelete,
        ReadOnlySpan<byte> value)
    {
        var previous = _index.TryGet(key, out var currentHead)
            ? currentHead
            : VersionHandle.Invalid;
        var previousPresent = previous.IsValid && !GetVersion(previous).Metadata.IsTombstone;
        var chainLength = previous.IsValid ? checked(GetVersion(previous).ChainLength + 1) : 1;
        var handle = AllocateHandle();
        var metadata = VersionMetadata.Committed(commitSequence, isTombstone: isDelete);
        var record = new CommittedVersionRecord(
            handle,
            key,
            creatorTransaction,
            metadata,
            previous,
            isDelete ? [] : value.ToArray(),
            chainLength);

        _versions.Add(handle, record);
        _index.Publish(key, handle);
        _maximumChainLength = Math.Max(_maximumChainLength, chainLength);

        if (isDelete)
        {
            if (previousPresent)
            {
                _currentKeyCount--;
            }
        }
        else if (!previousPresent)
        {
            _currentKeyCount++;
        }
    }

    private CommittedVersionRecord GetVersion(VersionHandle handle)
    {
        if (!_versions.TryGetValue(handle, out var version))
        {
            throw new InvalidOperationException($"Version index points to missing handle {handle.Value}.");
        }

        return version;
    }

    private VersionHandle AllocateHandle()
    {
        if (_nextHandle == ulong.MaxValue)
        {
            throw new InvalidOperationException("The managed version-handle space is exhausted.");
        }

        var handle = new VersionHandle(_nextHandle);
        _nextHandle++;
        return handle;
    }

    private static void EnsureCommittedSequence(CommitSequence commitSequence)
    {
        if (commitSequence.IsInitial)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commitSequence),
                "Committed history requires a non-zero commit sequence.");
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record CommittedVersionRecord(
        VersionHandle Handle,
        BinaryKey Key,
        TransactionId CreatorTransaction,
        VersionMetadata Metadata,
        VersionHandle Previous,
        byte[] Value,
        int ChainLength);
    private readonly record struct VersionIdentity(
        TransactionId TransactionId,
        CommitSequence CommitSequence,
        BinaryKey Key);
}

public readonly record struct CommittedVersionStoreStatistics(
    int VersionCount,
    int CurrentKeyCount,
    int ChainCount,
    double AverageChainLength,
    int MaximumChainLength,
    VersionIndexStatistics Index);

public sealed record CommittedVersionSnapshot(
    TransactionId TransactionId,
    CommitSequence CommitSequence,
    BinaryKey Key,
    bool IsDelete,
    ReadOnlyMemory<byte> Value);

public readonly record struct HistoryCompactionResult(
    int ReclaimedVersions,
    int RetainedVersions);
