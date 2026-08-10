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
}

public readonly record struct CommittedVersionStoreStatistics(
    int VersionCount,
    int CurrentKeyCount,
    int ChainCount,
    double AverageChainLength,
    int MaximumChainLength,
    VersionIndexStatistics Index);
