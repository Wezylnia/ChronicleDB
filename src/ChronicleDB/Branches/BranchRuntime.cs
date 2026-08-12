using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.History.Branches;
using ChronicleDB.History.Snapshots;
using ChronicleDB.Indexing.Baseline;
using ChronicleDB.Recovery;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.History;
using ChronicleDB.Storage.Snapshots;
using ChronicleDB.Transactions.Mvcc;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Branches;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB;

/// <summary>
/// Open branch-local runtime. From v0.8 onward branch.wal is the transaction
/// durability authority; branch metadata is lifecycle/cache metadata and the local
/// key-value file is a derived append-oriented representation of committed versions.
/// </summary>
internal sealed class BranchRuntime : IDisposable
{
    public const string WalFileName = "branch.wal";

    private int _disposed;
    private int _openBranchHandles;
    private int _activeTransactions;
    private int _openHistoricalHandles;

    private BranchRuntime(
        BranchDefinition definition,
        string directory,
        PersistentKeyValueStore store,
        WalLog wal,
        PersistentSnapshotStore snapshotStore,
        SnapshotCatalog snapshots,
        CommittedVersionStore versions,
        CommitSequence historyFloor)
    {
        Definition = definition;
        Directory = directory;
        Store = store;
        Wal = wal;
        SnapshotStore = snapshotStore;
        Snapshots = snapshots;
        Versions = versions;
        HistoryFloor = historyFloor;
    }

    public BranchDefinition Definition { get; private set; }
    public string Directory { get; }
    public PersistentKeyValueStore Store { get; }
    public WalLog Wal { get; }
    public PersistentSnapshotStore SnapshotStore { get; }
    public SnapshotCatalog Snapshots { get; }
    public CommittedVersionStore Versions { get; }
    public CommitSequence HistoryFloor { get; private set; }
    public object CommitGate { get; } = new();

    public int OpenBranchHandles => Volatile.Read(ref _openBranchHandles);
    public int ActiveTransactions => Volatile.Read(ref _activeTransactions);
    public int OpenHistoricalHandles => Volatile.Read(ref _openHistoricalHandles);
    public bool HasOpenHandles => OpenBranchHandles != 0 || ActiveTransactions != 0 || OpenHistoricalHandles != 0;

    public void AcquireBranchHandle() => Interlocked.Increment(ref _openBranchHandles);
    public void ReleaseBranchHandle() => DecrementNonNegative(ref _openBranchHandles, "branch handle");
    public void TransactionStarted() => Interlocked.Increment(ref _activeTransactions);
    public void TransactionCompleted() => DecrementNonNegative(ref _activeTransactions, "branch transaction");
    public void HistoricalHandleOpened() => Interlocked.Increment(ref _openHistoricalHandles);
    public void HistoricalHandleClosed() => DecrementNonNegative(ref _openHistoricalHandles, "historical handle");

    public void PublishDefinition(BranchDefinition definition)
    {
        if (definition.BranchId != Definition.BranchId || definition.HistoryId != Definition.HistoryId)
        {
            throw new InvalidOperationException("A branch runtime cannot change logical identity.");
        }
        Definition = definition;
    }

    public void AdvanceHistoryFloor(CommitSequence floor)
    {
        if (floor < HistoryFloor || floor > Definition.LocalCurrentSequence)
        {
            throw new InvalidOperationException("A branch history floor may only advance inside committed history.");
        }
        HistoryFloor = floor;
        Snapshots.AdvanceRetentionFloor(floor, Definition.LocalCurrentSequence);
    }

    private static (Guid OperationId, long StartedEventId) StartRecoveryPhase(
        ResearchEventPublisher? researchEvents,
        BranchDefinition definition,
        ResearchRecoveryPhaseKind phase,
        IReadOnlyList<string> resources,
        long dependencyEventId)
    {
        if (researchEvents is null || researchEvents.Mode == ResearchTelemetryMode.Disabled)
        {
            return (Guid.Empty, 0);
        }

        var operationId = Guid.NewGuid();
        researchEvents.TryPublish(
            logicalEventId => new ResearchEvent(
                logicalEventId,
                logicalEventId,
                ResearchEventKind.RecoveryPhaseStarted,
                definition.HistoryId,
                definition.ParentHistoryId,
                operationId,
                transactionId: null,
                resources,
                ResearchDurabilityPhase.None,
                definition.LocalCurrentSequence.Value,
                dependencyEventId > 0 ? [dependencyEventId] : [],
                logicalKeyId: null,
                versionId: null,
                offset: null,
                bytes: null,
                readObservation: null,
                new ResearchRecoveryPhaseObservation(phase)),
            out var startedEventId);
        return (operationId, startedEventId);
    }

    private static long CompleteRecoveryPhase(
        ResearchEventPublisher? researchEvents,
        BranchDefinition definition,
        ResearchRecoveryPhaseKind phase,
        IReadOnlyList<string> resources,
        Guid operationId,
        long startedEventId)
    {
        if (researchEvents is null
            || researchEvents.Mode == ResearchTelemetryMode.Disabled
            || operationId == Guid.Empty)
        {
            return startedEventId;
        }

        researchEvents.TryPublish(
            logicalEventId => new ResearchEvent(
                logicalEventId,
                logicalEventId,
                ResearchEventKind.RecoveryPhaseCompleted,
                definition.HistoryId,
                definition.ParentHistoryId,
                operationId,
                transactionId: null,
                resources,
                ResearchDurabilityPhase.None,
                definition.LocalCurrentSequence.Value,
                startedEventId > 0 ? [startedEventId] : [],
                logicalKeyId: null,
                versionId: null,
                offset: null,
                bytes: null,
                readObservation: null,
                new ResearchRecoveryPhaseObservation(phase)),
            out var completedEventId);
        return completedEventId > 0 ? completedEventId : startedEventId;
    }

    public static BranchRuntime Open(
        string databaseDirectory,
        BranchDefinition definition,
        BranchStoreRecord publishedState,
        IReadOnlyList<BranchCommitDescriptor> legacyCommits,
        PersistentBranchMetadataStore branchStore,
        StorageOptions databaseOptions,
        ResearchEventPublisher? researchEvents = null,
        long recoveryDependencyEventId = 0)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(publishedState);
        ArgumentNullException.ThrowIfNull(legacyCommits);
        ArgumentNullException.ThrowIfNull(branchStore);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        var directory = BranchStorageLayout.GetDirectory(databaseDirectory, definition.BranchId);
        if (!System.IO.Directory.Exists(directory))
        {
            throw new StorageCorruptionException($"Branch {definition.BranchId.Value} local storage directory is missing.");
        }

        var localOptions = BranchStorageLayout.CreateLocalStorageOptions(databaseOptions);
        PersistentKeyValueStore? store = null;
        WalLog? wal = null;
        PersistentSnapshotStore? snapshotStore = null;
        CommittedVersionStore? versions = null;
        try
        {
            var phaseDependency = recoveryDependencyEventId;
            var phase = StartRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.LocalStoreOpen,
                [$"branch-{definition.BranchId.Value:N}-data"],
                phaseDependency);
            store = PersistentKeyValueStore.Open(directory, localOptions, allowIncompleteFinalPage: true);
            if (store.DatabaseId != definition.LocalStorageId)
            {
                throw new StorageCorruptionException($"Branch {definition.BranchId.Value} local storage identity does not match metadata.");
            }
            phaseDependency = CompleteRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.LocalStoreOpen,
                [$"branch-{definition.BranchId.Value:N}-data"],
                phase.OperationId,
                phase.StartedEventId);

            phase = StartRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.WalAuthorityOpen,
                [$"branch-{definition.BranchId.Value:N}-wal"],
                phaseDependency);
            var walPath = Path.Combine(directory, WalFileName);
            var walInitialized = store.HasFormatFlag(DatabaseHeader.WalInitializedFlag);
            if (!walInitialized && File.Exists(walPath))
            {
                // A crash during the one-time v0.7 -> v0.8 bootstrap may leave a
                // non-authoritative partial WAL. The capability flag was deliberately
                // not published yet, therefore metadata + local data remain authority.
                File.Delete(walPath);
            }
            if (walInitialized && !File.Exists(walPath))
            {
                throw new StorageCorruptionException("Branch metadata says WAL is initialized, but branch.wal is missing.");
            }

            wal = WalLog.Open(
                directory,
                definition.LocalStorageId,
                new WalOptions { FileName = WalFileName, FlushOnAppend = false });

            if (!walInitialized)
            {
                var legacy = ReadLegacyCommittedHistory(definition, legacyCommits, store);
                foreach (var commit in legacy)
                {
                    RecoveredLogicalHistoryValidator.ValidateMutations(
                        commit.Mutations,
                        databaseOptions,
                        "Legacy branch history");
                }
                BootstrapWal(definition, wal, legacy);
                wal.Flush();
                store.EnsureFormatFlags(DatabaseHeader.WalInitializedFlag);
            }
            phaseDependency = CompleteRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.WalAuthorityOpen,
                [$"branch-{definition.BranchId.Value:N}-wal"],
                phase.OperationId,
                phase.StartedEventId);

            phase = StartRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.CheckpointLoadAndReplay,
                [$"branch-{definition.BranchId.Value:N}-checkpoint"],
                phaseDependency);
            HistoryCheckpoint? checkpoint = null;
            var checkpointPath = Path.Combine(directory, PersistentHistoryCheckpoint.FileName);
            if (store.HasFormatFlag(DatabaseHeader.HistoryCheckpointInitializedFlag))
            {
                checkpoint = PersistentHistoryCheckpoint.TryLoad(
                    directory,
                    definition.LocalStorageId,
                    definition.HistoryId)
                    ?? throw new StorageCorruptionException("Branch history checkpoint is required but missing.");
            }
            else if (File.Exists(checkpointPath) || File.Exists(checkpointPath + ".previous"))
            {
                // An unpublished checkpoint is never recovery authority because WAL
                // has not yet been reset. Ignore and remove it deterministically.
                TryDelete(checkpointPath);
                TryDelete(checkpointPath + ".previous");
            }

            var baseSequence = checkpoint?.CheckpointSequence ?? CommitSequence.Initial;
            var historyFloor = checkpoint?.RetentionFloor ?? CommitSequence.Initial;
            versions = new CommittedVersionStore(new SynchronizedVersionIndex());
            if (checkpoint is not null)
            {
                RecoveredLogicalHistoryValidator.ValidateCheckpoint(
                    checkpoint,
                    databaseOptions,
                    $"Branch {definition.BranchId.Value} history checkpoint");
                ReplayCheckpoint(checkpoint, versions);
            }
            phaseDependency = CompleteRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.CheckpointLoadAndReplay,
                [$"branch-{definition.BranchId.Value:N}-checkpoint"],
                phase.OperationId,
                phase.StartedEventId);

            phase = StartRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.WalReplay,
                [$"branch-{definition.BranchId.Value:N}-wal"],
                phaseDependency);
            var checkpointTransactionIds = checkpoint?.Versions
                .Select(version => version.TransactionId)
                .ToHashSet();
            var recovery = BranchWalRecovery.ReadCommitted(
                wal,
                definition.BranchId,
                definition.HistoryId,
                baseSequence,
                localOptions.PageSize,
                checkpointTransactionIds);
            foreach (var committed in recovery.CommittedTransactions)
            {
                RecoveredLogicalHistoryValidator.ValidateMutations(
                    committed.Mutations,
                    databaseOptions,
                    $"Branch {definition.BranchId.Value} WAL");
                versions.ValidateReplayCapacity(committed.Mutations);
                versions.ReplayCommitted(committed.TransactionId, committed.CommitSequence, committed.Mutations);
            }
            phaseDependency = CompleteRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.WalReplay,
                [$"branch-{definition.BranchId.Value:N}-wal"],
                phase.OperationId,
                phase.StartedEventId);

            phase = StartRecoveryPhase(
                researchEvents,
                definition,
                ResearchRecoveryPhaseKind.PhysicalStateValidation,
                [$"branch-{definition.BranchId.Value:N}-data", "branch-catalog"],
                phaseDependency);
            if (definition.LocalCurrentSequence > recovery.CurrentCommitSequence)
            {
                throw new StorageCorruptionException(
                    "Branch lifecycle metadata claims commits that are absent from the authoritative branch WAL/checkpoint history.");
            }

            // Remove orphaned local append bytes after the last metadata publication.
            // A smaller complete file can be the result of a crash after copy-and-publish
            // compaction but before its physical-boundary metadata record. Because branch
            // data is derived state, accept that case only after proving that every retained
            // authoritative WAL/checkpoint version is present and byte-identical.
            var publishedLength = publishedState.DataLengthAfterCommit;
            var rebuiltPhysicalFromAuthority = false;
            if (store.HasUntrustedTail
                && store.UntrustedTailOffset is { } corruptOffset
                && corruptOffset < publishedLength)
            {
                // v0.8 makes branch.wal/checkpoint the transaction authority. A checksum
                // or framing failure inside an already-published branch data prefix is
                // therefore recoverable derived-state damage rather than a reason to lose
                // the branch. Rebuild only after the authoritative logical history above
                // has been fully validated; semantic mismatches in otherwise valid pages
                // still fail closed in ValidatePhysicalHistory.
                var desired = EncodePhysicalState(definition, versions.SnapshotHistory());
                store.RewriteStateForRecovery(desired);
                branchStore.AppendPhysicalBoundary(definition.BranchId, store.DataLength);
                publishedLength = store.DataLength;
                rebuiltPhysicalFromAuthority = true;
            }
            else if (store.DataLength < publishedLength)
            {
                ValidatePhysicalHistory(definition, store, versions, historyFloor);
                branchStore.AppendPhysicalBoundary(definition.BranchId, store.DataLength);
                publishedLength = store.DataLength;
            }
            else if (store.DataLength != publishedLength || store.HasUntrustedTail)
            {
                store.RecoverAppendOnlyPrefix(publishedLength);
            }

            var repairedDefinition = definition;
            foreach (var committed in recovery.CommittedTransactions
                         .Where(tx => tx.CommitSequence > repairedDefinition.LocalCurrentSequence)
                         .OrderBy(tx => tx.CommitSequence.Value))
            {
                if (!rebuiltPhysicalFromAuthority)
                {
                    var physical = EncodePhysicalMutations(repairedDefinition, committed);
                    store.ValidateBatch(physical);
                    store.ApplyBatch(physical);
                }

                var nextLength = store.DataLength;
                branchStore.AppendAdvance(
                    repairedDefinition.BranchId,
                    committed.CommitSequence,
                    committed.TransactionId,
                    committed.Mutations.Count,
                    nextLength);
                repairedDefinition = repairedDefinition.WithCurrentSequence(committed.CommitSequence);
            }

            if (repairedDefinition.LocalCurrentSequence != recovery.CurrentCommitSequence)
            {
                throw new StorageCorruptionException("Branch recovery did not converge metadata and WAL histories.");
            }

            ValidatePhysicalHistory(repairedDefinition, store, versions, historyFloor);
            phaseDependency = CompleteRecoveryPhase(
                researchEvents,
                repairedDefinition,
                ResearchRecoveryPhaseKind.PhysicalStateValidation,
                [$"branch-{definition.BranchId.Value:N}-data", "branch-catalog"],
                phase.OperationId,
                phase.StartedEventId);

            phase = StartRecoveryPhase(
                researchEvents,
                repairedDefinition,
                ResearchRecoveryPhaseKind.SnapshotMetadataOpen,
                [$"branch-{definition.BranchId.Value:N}-snapshots", "history-roots"],
                phaseDependency);
            var snapshotPath = Path.Combine(directory, PersistentSnapshotStore.FileName);
            if (store.HasFormatFlag(DatabaseHeader.SnapshotStoreInitializedFlag) && !File.Exists(snapshotPath))
            {
                throw new StorageCorruptionException($"Branch {definition.BranchId.Value} requires snapshot metadata, but the file is missing.");
            }

            snapshotStore = PersistentSnapshotStore.Open(
                directory,
                definition.LocalStorageId,
                CommitSequence.Initial,
                databaseOptions.FaultInjector);
            if (snapshotStore.MaximumReferencedSequence > repairedDefinition.LocalCurrentSequence)
            {
                throw new StorageCorruptionException($"Branch {definition.BranchId.Value} snapshot metadata references future local history.");
            }

            store.EnsureFormatFlags(DatabaseHeader.SnapshotStoreInitializedFlag);
            var records = snapshotStore.ListActive();
            _ = CompleteRecoveryPhase(
                researchEvents,
                repairedDefinition,
                ResearchRecoveryPhaseKind.SnapshotMetadataOpen,
                [$"branch-{definition.BranchId.Value:N}-snapshots", "history-roots"],
                phase.OperationId,
                phase.StartedEventId);
            var snapshots = new SnapshotCatalog(
                historyFloor,
                repairedDefinition.LocalCurrentSequence,
                records.Select(record => new SnapshotDefinition(
                    record.SnapshotId,
                    record.Name,
                    record.Sequence,
                    record.CreatedUnixMilliseconds)));
            return new BranchRuntime(
                repairedDefinition,
                directory,
                store,
                wal,
                snapshotStore,
                snapshots,
                versions,
                historyFloor);
        }
        catch
        {
            versions?.Dispose();
            snapshotStore?.Dispose();
            wal?.Dispose();
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
                Wal.Dispose();
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
    }

    internal static byte[] WrapPayload(BranchDefinition definition, ReadOnlySpan<byte> payload)
        => BranchWalEnvelopeCodec.Encode(definition.BranchId, definition.HistoryId, payload);

    private static void BootstrapWal(
        BranchDefinition definition,
        WalLog wal,
        IReadOnlyList<LegacyBranchCommit> commits)
    {
        long baseLength = 0;
        foreach (var commit in commits.OrderBy(item => item.CommitSequence.Value))
        {
            wal.Append(WalRecordType.Begin, commit.TransactionId, WrapPayload(definition, []));
            foreach (var mutation in commit.Mutations)
            {
                var inner = mutation.IsDelete
                    ? WalMutationCodec.EncodeDelete(mutation.Key)
                    : WalMutationCodec.EncodePut(mutation.Key, mutation.Value.Span);
                wal.Append(
                    mutation.IsDelete ? WalRecordType.Delete : WalRecordType.Put,
                    commit.TransactionId,
                    WrapPayload(definition, inner));
            }
            wal.Append(
                WalRecordType.Commit,
                commit.TransactionId,
                WrapPayload(definition, WalCommitCodec.Encode(commit.CommitSequence, baseLength)));
            baseLength = commit.DataLengthAfterCommit;
        }
    }

    private static List<LegacyBranchCommit> ReadLegacyCommittedHistory(
        BranchDefinition definition,
        IReadOnlyList<BranchCommitDescriptor> commits,
        PersistentKeyValueStore store)
    {
        if (definition.LocalCurrentSequence.Value > int.MaxValue
            || commits.Count != (int)definition.LocalCurrentSequence.Value)
        {
            throw new StorageCorruptionException("Legacy branch metadata does not form a complete local sequence prefix.");
        }

        long previousLength = 0;
        foreach (var descriptor in commits)
        {
            if (descriptor.DataLengthAfterCommit < previousLength || descriptor.DataLengthAfterCommit > store.DataLength)
            {
                throw new StorageCorruptionException("Legacy branch physical commit boundaries are invalid.");
            }
            previousLength = descriptor.DataLengthAfterCommit;
        }
        if (store.DataLength != previousLength || store.HasUntrustedTail)
        {
            store.RecoverAppendOnlyPrefix(previousLength);
        }

        var recordsBySequence = DecodePhysicalRecords(definition, store);
        var result = new List<LegacyBranchCommit>(commits.Count);
        foreach (var descriptor in commits)
        {
            recordsBySequence.TryGetValue(descriptor.CommitSequence, out var records);
            records ??= [];
            ValidateMutationSet(descriptor.TransactionId, descriptor.MutationCount, records);
            var mutations = records.OrderBy(record => record.MutationIndex)
                .Select(record => new StorageMutation(new BinaryKey(record.Key), record.IsDelete, record.Value))
                .ToArray();
            result.Add(new LegacyBranchCommit(
                descriptor.TransactionId,
                descriptor.CommitSequence,
                descriptor.DataLengthAfterCommit,
                mutations));
        }
        return result;
    }

    private static void ReplayCheckpoint(HistoryCheckpoint checkpoint, CommittedVersionStore versions)
    {
        foreach (var transaction in checkpoint.Versions
                     .GroupBy(version => (version.CommitSequence, version.TransactionId))
                     .OrderBy(group => group.Key.CommitSequence.Value))
        {
            var mutations = transaction.Select(version => new StorageMutation(
                version.Key,
                version.IsDelete,
                version.Value.Span)).ToArray();
            versions.ValidateReplayCapacity(mutations);
            versions.ReplayCommitted(transaction.Key.TransactionId, transaction.Key.CommitSequence, mutations);
        }
    }

    private static List<StorageMutation> EncodePhysicalMutations(
        BranchDefinition definition,
        RecoveredBranchTransaction committed)
    {
        var result = new List<StorageMutation>(committed.Mutations.Count);
        for (var i = 0; i < committed.Mutations.Count; i++)
        {
            var mutation = committed.Mutations[i];
            var record = new BranchVersionRecord(
                definition.BranchId,
                definition.HistoryId,
                committed.TransactionId,
                committed.CommitSequence,
                i,
                committed.Mutations.Count,
                mutation.Key.ToArray(),
                mutation.IsDelete,
                mutation.IsDelete ? [] : mutation.Value.ToArray());
            result.Add(new StorageMutation(
                ChronicleDatabase.CreateBranchPhysicalVersionKey(committed.CommitSequence, committed.TransactionId, i),
                isDelete: false,
                BranchVersionRecordCodec.Encode(record)));
        }
        return result;
    }

    internal static IReadOnlyList<StorageMutation> EncodePhysicalState(
        BranchDefinition definition,
        IReadOnlyList<CommittedVersionSnapshot> history)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(history);

        var result = new List<StorageMutation>(history.Count);
        foreach (var transaction in history
                     .GroupBy(version => (version.CommitSequence, version.TransactionId))
                     .OrderBy(group => group.Key.CommitSequence.Value)
                     .ThenBy(group => group.Key.TransactionId.Value))
        {
            var mutations = transaction
                .OrderBy(version => version.Key, BinaryKeyLexicographicComparer.Instance)
                .ToArray();
            for (var index = 0; index < mutations.Length; index++)
            {
                var version = mutations[index];
                var record = new BranchVersionRecord(
                    definition.BranchId,
                    definition.HistoryId,
                    version.TransactionId,
                    version.CommitSequence,
                    index,
                    mutations.Length,
                    version.Key.ToArray(),
                    version.IsDelete,
                    version.IsDelete ? [] : version.Value.ToArray());
                result.Add(new StorageMutation(
                    ChronicleDatabase.CreateBranchPhysicalVersionKey(
                        version.CommitSequence,
                        version.TransactionId,
                        index),
                    isDelete: false,
                    BranchVersionRecordCodec.Encode(record)));
            }
        }

        return result;
    }

    private static void ValidatePhysicalHistory(
        BranchDefinition definition,
        PersistentKeyValueStore store,
        CommittedVersionStore versions,
        CommitSequence historyFloor)
    {
        var expected = versions.SnapshotHistory()
            .ToDictionary(
                version => (version.CommitSequence, version.TransactionId, version.Key),
                version => version);
        var actual = DecodePhysicalRecords(definition, store)
            .SelectMany(pair => pair.Value)
            .ToArray();
        var seenExpected = new HashSet<(CommitSequence, TransactionId, BinaryKey)>();

        foreach (var record in actual)
        {
            var key = (record.CommitSequence, record.TransactionId, new BinaryKey(record.Key));
            if (!expected.TryGetValue(key, out var version))
            {
                // A freshly published retained-history checkpoint may intentionally make
                // records below the generic floor unnecessary before physical compaction.
                // Anything at/above the retained range, or beyond current logical history,
                // must be explained by the authoritative WAL/checkpoint projection.
                if (record.CommitSequence >= historyFloor
                    || record.CommitSequence > definition.LocalCurrentSequence)
                {
                    throw new StorageCorruptionException(
                        "Branch physical history contains an unexplained retained/future version.");
                }
                continue;
            }

            if (version.IsDelete != record.IsDelete
                || !version.Value.Span.SequenceEqual(record.Value))
            {
                throw new StorageCorruptionException("Branch physical version disagrees with WAL/checkpoint history.");
            }
            if (!seenExpected.Add(key))
            {
                throw new StorageCorruptionException(
                    "Branch physical history contains a duplicate retained logical version.");
            }
        }

        if (seenExpected.Count != expected.Count)
        {
            throw new StorageCorruptionException("Branch physical history is missing authoritative retained versions.");
        }
    }

    private static Dictionary<CommitSequence, List<BranchVersionRecord>> DecodePhysicalRecords(
        BranchDefinition definition,
        PersistentKeyValueStore store)
    {
        var result = new Dictionary<CommitSequence, List<BranchVersionRecord>>();
        foreach (var physical in store.SnapshotCurrentState())
        {
            var record = BranchVersionRecordCodec.Decode(physical.Value.Span);
            if (record.BranchId != definition.BranchId || record.HistoryId != definition.HistoryId)
            {
                throw new StorageCorruptionException("Branch-local version belongs to another history domain.");
            }

            var expectedPhysicalKey = ChronicleDatabase.CreateBranchPhysicalVersionKey(
                record.CommitSequence,
                record.TransactionId,
                record.MutationIndex);
            if (physical.Key != expectedPhysicalKey)
            {
                throw new StorageCorruptionException(
                    "Branch-local physical key does not match the encoded version identity.");
            }

            if (!result.TryGetValue(record.CommitSequence, out var list))
            {
                list = [];
                result.Add(record.CommitSequence, list);
            }
            list.Add(record);
        }
        return result;
    }

    private static void ValidateMutationSet(
        TransactionId transactionId,
        int mutationCount,
        List<BranchVersionRecord> records)
    {
        if (records.Count != mutationCount)
        {
            throw new StorageCorruptionException("Branch commit has incomplete physical version data.");
        }
        var indexes = new HashSet<int>();
        foreach (var record in records)
        {
            if (record.TransactionId != transactionId
                || record.MutationCount != mutationCount
                || !indexes.Add(record.MutationIndex))
            {
                throw new StorageCorruptionException("Branch physical mutation identity/index metadata is invalid.");
            }
        }
        for (var i = 0; i < mutationCount; i++)
        {
            if (!indexes.Contains(i))
            {
                throw new StorageCorruptionException("Branch physical mutation indexes are discontinuous.");
            }
        }
    }

    private static void DecrementNonNegative(ref int value, string name)
    {
        var next = Interlocked.Decrement(ref value);
        if (next < 0)
        {
            Interlocked.Exchange(ref value, 0);
            throw new InvalidOperationException($"Branch {name} lifetime accounting underflowed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file is not authoritative until the corresponding capability flag is
            // published. Cleanup failure may leave an orphan, but must not alter recovery.
        }
    }

    private sealed record LegacyBranchCommit(
        TransactionId TransactionId,
        CommitSequence CommitSequence,
        long DataLengthAfterCommit,
        IReadOnlyList<StorageMutation> Mutations);
}

internal static class BranchStorageLayout
{
    public const string DirectoryName = "branches";

    public static string GetDirectory(string databaseDirectory, BranchId branchId)
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
