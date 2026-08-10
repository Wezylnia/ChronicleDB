using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.History.Branches;
using ChronicleDB.History.Snapshots;
using ChronicleDB.Indexing.Baseline;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.Snapshots;
using ChronicleDB.Transactions.Mvcc;

namespace ChronicleDB;

/// <summary>
/// Open v0.7 branch-local state. The branch metadata journal is authoritative
/// for committed append boundaries; data beyond the latest published boundary
/// is orphaned pre-commit state and is removed on reopen.
/// </summary>
internal sealed class BranchRuntime : IDisposable
{
    private int _disposed;

    private BranchRuntime(
        BranchDefinition definition,
        string directory,
        PersistentKeyValueStore store,
        PersistentSnapshotStore snapshotStore,
        SnapshotCatalog snapshots,
        CommittedVersionStore versions)
    {
        Definition = definition;
        Directory = directory;
        Store = store;
        SnapshotStore = snapshotStore;
        Snapshots = snapshots;
        Versions = versions;
    }

    public BranchDefinition Definition { get; private set; }

    public string Directory { get; }

    public PersistentKeyValueStore Store { get; }

    public PersistentSnapshotStore SnapshotStore { get; }

    public SnapshotCatalog Snapshots { get; }

    public CommittedVersionStore Versions { get; }

    public object CommitGate { get; } = new();

    public void PublishDefinition(BranchDefinition definition)
    {
        if (definition.BranchId != Definition.BranchId || definition.HistoryId != Definition.HistoryId)
        {
            throw new InvalidOperationException("A branch runtime cannot change logical identity.");
        }

        Definition = definition;
    }

    public static BranchRuntime Open(
        string databaseDirectory,
        BranchDefinition definition,
        IReadOnlyList<BranchCommitDescriptor> commits,
        StorageOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(databaseOptions);
        var directory = BranchStorageLayout.GetDirectory(databaseDirectory, definition.BranchId);
        if (!System.IO.Directory.Exists(directory))
        {
            throw new StorageCorruptionException(
                $"Branch {definition.BranchId.Value} local storage directory is missing.");
        }

        var localOptions = BranchStorageLayout.CreateLocalStorageOptions(databaseOptions);
        PersistentKeyValueStore? store = null;
        PersistentSnapshotStore? snapshotStore = null;
        CommittedVersionStore? versions = null;
        try
        {
            store = PersistentKeyValueStore.Open(directory, localOptions, allowIncompleteFinalPage: true);
            if (store.DatabaseId != definition.LocalStorageId)
            {
                throw new StorageCorruptionException(
                    $"Branch {definition.BranchId.Value} local storage identity does not match metadata.");
            }

            var expectedLength = commits.Count == 0 ? 0 : commits[^1].DataLengthAfterCommit;

            if (store.DataLength < expectedLength)
            {
                throw new StorageCorruptionException(
                    "Branch metadata references committed local data that is physically missing.");
            }

            if (store.DataLength != expectedLength || store.HasUntrustedTail)
            {
                store.RecoverAppendOnlyPrefix(expectedLength);
            }

            versions = new CommittedVersionStore(new SynchronizedVersionIndex());
            ReplayCommittedHistory(definition, commits, store, versions);

            var snapshotPath = Path.Combine(directory, PersistentSnapshotStore.FileName);
            if (store.HasFormatFlag(DatabaseHeader.SnapshotStoreInitializedFlag) && !File.Exists(snapshotPath))
            {
                throw new StorageCorruptionException(
                    $"Branch {definition.BranchId.Value} requires snapshot metadata, but the file is missing.");
            }

            snapshotStore = PersistentSnapshotStore.Open(
                directory,
                definition.LocalStorageId,
                CommitSequence.Initial,
                databaseOptions.FaultInjector);
            if (snapshotStore.MaximumReferencedSequence > definition.LocalCurrentSequence)
            {
                throw new StorageCorruptionException(
                    $"Branch {definition.BranchId.Value} snapshot metadata references future local history.");
            }

            store.EnsureFormatFlags(DatabaseHeader.SnapshotStoreInitializedFlag);
            var records = snapshotStore.ListActive();
            var snapshots = new SnapshotCatalog(
                CommitSequence.Initial,
                definition.LocalCurrentSequence,
                records.Select(record => new SnapshotDefinition(
                    record.SnapshotId,
                    record.Name,
                    record.Sequence,
                    record.CreatedUnixMilliseconds)));
            return new BranchRuntime(definition, directory, store, snapshotStore, snapshots, versions);
        }
        catch
        {
            versions?.Dispose();
            snapshotStore?.Dispose();
            store?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            SnapshotStore.Dispose();
        }
        finally
        {
            try
            {
                Store.Dispose();
            }
            finally
            {
                Versions.Dispose();
            }
        }
    }

    private static void ReplayCommittedHistory(
        BranchDefinition definition,
        IReadOnlyList<BranchCommitDescriptor> commits,
        PersistentKeyValueStore store,
        CommittedVersionStore versions)
    {
        var bySequence = commits.ToDictionary(commit => commit.CommitSequence);
        if (definition.LocalCurrentSequence.Value > int.MaxValue
            || commits.Count != (int)definition.LocalCurrentSequence.Value)
        {
            throw new StorageCorruptionException(
                "Branch commit metadata does not form a complete local sequence prefix.");
        }

        long previousLength = 0;
        for (var i = 0; i < commits.Count; i++)
        {
            var descriptor = commits[i];
            if (descriptor.CommitSequence.Value != checked((ulong)i + 1)
                || descriptor.DataLengthAfterCommit < previousLength
                || descriptor.DataLengthAfterCommit > store.DataLength)
            {
                throw new StorageCorruptionException("Branch commit descriptors are not monotonic.");
            }
            previousLength = descriptor.DataLengthAfterCommit;
        }

        var recordsBySequence = new Dictionary<CommitSequence, List<BranchVersionRecord>>();
        foreach (var physical in store.SnapshotCurrentState())
        {
            var record = BranchVersionRecordCodec.Decode(physical.Value.Span);
            if (record.BranchId != definition.BranchId || record.HistoryId != definition.HistoryId)
            {
                throw new StorageCorruptionException("Branch-local version belongs to another history domain.");
            }

            if (!bySequence.TryGetValue(record.CommitSequence, out var descriptor)
                || descriptor.TransactionId != record.TransactionId
                || descriptor.MutationCount != record.MutationCount)
            {
                throw new StorageCorruptionException("Branch-local version is not covered by durable branch metadata.");
            }

            if (!recordsBySequence.TryGetValue(record.CommitSequence, out var list))
            {
                list = [];
                recordsBySequence.Add(record.CommitSequence, list);
            }
            list.Add(record);
        }

        foreach (var descriptor in commits)
        {
            recordsBySequence.TryGetValue(descriptor.CommitSequence, out var records);
            records ??= [];
            if (records.Count != descriptor.MutationCount)
            {
                throw new StorageCorruptionException(
                    $"Branch commit {descriptor.CommitSequence.Value} has incomplete local version data.");
            }

            var ordered = records.OrderBy(record => record.MutationIndex).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].MutationIndex != i)
                {
                    throw new StorageCorruptionException("Branch mutation indexes are duplicated or discontinuous.");
                }
            }

            var mutations = ordered.Select(record => new StorageMutation(
                new BinaryKey(record.Key),
                record.IsDelete,
                record.Value)).ToArray();
            versions.ValidateReplayCapacity(mutations);
            versions.ReplayCommitted(descriptor.TransactionId, descriptor.CommitSequence, mutations);
        }
    }
}

internal static class BranchStorageLayout
{
    public const string DirectoryName = "branches";

    public static string GetDirectory(string databaseDirectory, ChronicleDB.Core.Identifiers.BranchId branchId)
        => Path.Combine(databaseDirectory, DirectoryName, branchId.Value.ToString("N"));

    public static StorageOptions CreateLocalStorageOptions(StorageOptions parent)
    {
        var envelopeMax = checked(parent.MaxValueSize + parent.MaxKeySize + BranchVersionRecordCodec.HeaderSize);
        if (envelopeMax > StorageOptions.AbsoluteMaxValueSize)
        {
            throw new StorageLimitException("Configured value/key limits cannot fit inside a branch version envelope.");
        }

        return parent with
        {
            MaxKeySize = Math.Max(32, parent.MaxKeySize),
            MaxValueSize = envelopeMax,
            FlushOnWrite = true,
        };
    }
}
