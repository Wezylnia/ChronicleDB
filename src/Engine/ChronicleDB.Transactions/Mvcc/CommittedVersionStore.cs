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
/// Managed v0.3 version-chain store. The index only locates chain heads; this type
/// remains authoritative for MVCC visibility and immutable committed history.
/// </summary>
public sealed class CommittedVersionStore
{
    private readonly object _gate = new();
    private readonly IVersionIndex _index;
    private readonly Dictionary<VersionHandle, CommittedVersionRecord> _versions = [];
    private ulong _nextHandle = 1;

    public CommittedVersionStore(IVersionIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public int VersionCount
    {
        get
        {
            lock (_gate)
            {
                return _versions.Count;
            }
        }
    }

    public bool TryRead(BinaryKey key, CommitSequence visibilityBoundary, out byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (!_index.TryGet(key, out var current))
            {
                value = [];
                return false;
            }

            while (current.IsValid)
            {
                var version = GetVersion(current);
                if (VersionVisibility.IsVisible(version.Metadata, visibilityBoundary))
                {
                    if (version.Metadata.IsTombstone)
                    {
                        value = [];
                        return false;
                    }

                    value = version.Value.ToArray();
                    return true;
                }

                current = version.Previous;
            }

            value = [];
            return false;
        }
    }

    public bool TryGetLatestCommitSequence(BinaryKey key, out CommitSequence commitSequence)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (!_index.TryGet(key, out var head))
            {
                commitSequence = CommitSequence.Initial;
                return false;
            }

            commitSequence = GetVersion(head).Metadata.CommitSequence;
            return true;
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

        lock (_gate)
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

        lock (_gate)
        {
            // Malformed or legacy WALs can contain repeated mutations for one key in a
            // transaction. A transaction contributes one logical version per key: its
            // final local write.
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
        var handle = AllocateHandle();
        var metadata = VersionMetadata.Committed(commitSequence, isTombstone: isDelete);
        var record = new CommittedVersionRecord(
            handle,
            key,
            creatorTransaction,
            metadata,
            previous,
            isDelete ? [] : value.ToArray());

        _versions.Add(handle, record);
        _index.Publish(key, handle);
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

    private sealed record CommittedVersionRecord(
        VersionHandle Handle,
        BinaryKey Key,
        TransactionId CreatorTransaction,
        VersionMetadata Metadata,
        VersionHandle Previous,
        byte[] Value);
}
