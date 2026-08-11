using System.Collections.Concurrent;
using System.Diagnostics;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Diagnostics;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.History.Branches;
using ChronicleDB.History.Roots;
using ChronicleDB.History.Snapshots;
using ChronicleDB.Indexing.Baseline;
using ChronicleDB.Recovery;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.History;
using ChronicleDB.Storage.HistoryRoots;
using ChronicleDB.Storage.Snapshots;
using ChronicleDB.Transactions;
using ChronicleDB.Transactions.Faults;
using ChronicleDB.Transactions.Mvcc;
using ChronicleDB.Transactions.State;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB;

/// <summary>
/// Embedded persistent key-value engine implementing durable Snapshot Isolation,
/// persistent historical state, independently writable branches, retained time travel,
/// crash recovery, garbage collection, and copy-and-publish compaction.
/// </summary>
public sealed partial class ChronicleDatabase : IDisposable
{
    private readonly PersistentKeyValueStore _store;
    private readonly WalLog _wal;
    private readonly PersistentSnapshotStore _snapshotStore;
    private readonly PersistentHistoryRootStore _historyRootStore;
    private readonly SnapshotCatalog _snapshots;
    private readonly HistoryRootRegistry _historyRoots;
    private readonly CommittedVersionStore _versions;
    private readonly ReaderWriterLockSlim _lifecycle = new(LockRecursionPolicy.NoRecursion);
    private readonly object _stateGate = new();
    private readonly object _commitGate = new();
    private readonly object _historyGate = new();
    private readonly ActiveHistoryBoundaryRegistry _activeHistoryBoundaries = new();
    private readonly ITransactionFaultInjector? _faultInjector;
    private readonly EngineCounters _counters;
    private readonly ResearchEventPublisher _researchEvents;
    private readonly Guid _researchOperationId;
    private readonly Guid _databaseId;
    private readonly HistoryId _mainHistoryId;
    private CommitSequence _currentCommitSequence;
    private DatabaseState _state = DatabaseState.Open;

    private ChronicleDatabase(
        PersistentKeyValueStore store,
        WalLog wal,
        PersistentSnapshotStore snapshotStore,
        PersistentHistoryRootStore historyRootStore,
        SnapshotCatalog snapshots,
        HistoryRootRegistry historyRoots,
        PersistentBranchMetadataStore branchStore,
        BranchCatalog branches,
        ConcurrentDictionary<BranchId, BranchRuntime> branchRuntimes,
        string databaseDirectory,
        StorageOptions storageOptions,
        CommittedVersionStore versions,
        CommitSequence currentCommitSequence,
        ITransactionFaultInjector? faultInjector,
        EngineCounters counters,
        ResearchEventPublisher researchEvents,
        Guid researchOperationId)
    {
        _store = store;
        _wal = wal;
        _snapshotStore = snapshotStore;
        _historyRootStore = historyRootStore;
        _snapshots = snapshots;
        _historyRoots = historyRoots;
        _branchStore = branchStore;
        _branches = branches;
        _branchRuntimes = branchRuntimes;
        _databaseDirectory = databaseDirectory;
        _storageOptions = storageOptions;
        _versions = versions;
        _currentCommitSequence = currentCommitSequence;
        _faultInjector = faultInjector;
        _counters = counters;
        _researchEvents = researchEvents;
        _researchOperationId = researchOperationId;
        _databaseId = store.DatabaseId;
        _mainHistoryId = new HistoryId(_databaseId);
    }

    public Guid DatabaseId => _databaseId;

    public CommitSequence CurrentCommitSequence
    {
        get
        {
            EnterOperation();
            try
            {
                return GetCurrentCommitSequence();
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    public ulong HistoricalRetentionFloor
    {
        get
        {
            EnterOperation();
            try
            {
                return GetHistoryRetentionFloor().Value;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    public DatabaseState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public int Count
    {
        get
        {
            EnterOperation();
            try
            {
                return _versions.CurrentKeyCount;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    public static ChronicleDatabase Open(
        string directory,
        StorageOptions? options = null,
        ITransactionFaultInjector? faultInjector = null,
        IResearchEventSink? researchEventSink = null)
    {
        if (options is { MaxValueSize: > WalMutationCodec.MaxValueSize })
        {
            throw new StorageLimitException(
                $"ChronicleDB transactions support values up to {WalMutationCodec.MaxValueSize} bytes.");
        }

        var validatedOptions = options ?? new StorageOptions();
        var fullDirectory = Path.GetFullPath(directory);
        var researchEvents = new ResearchEventPublisher(researchEventSink);
        var researchOperationId = Guid.NewGuid();
        var store = PersistentKeyValueStore.Open(fullDirectory, validatedOptions, allowIncompleteFinalPage: true);
        WalLog? wal = null;
        PersistentSnapshotStore? snapshotStore = null;
        PersistentHistoryRootStore? historyRootStore = null;
        PersistentBranchMetadataStore? branchStore = null;
        ConcurrentDictionary<BranchId, BranchRuntime>? branchRuntimes = null;
        CommittedVersionStore? versions = null;
        try
        {
            var walWasInitialized = store.HasFormatFlag(DatabaseHeader.WalInitializedFlag);
            RequireInitializedFileIfFlagged(
                store,
                DatabaseHeader.WalInitializedFlag,
                Path.Combine(fullDirectory, WalOptions.DefaultFileName),
                "WAL");
            wal = WalLog.Open(fullDirectory, store.DatabaseId, new WalOptions { FlushOnAppend = false });

            var mainHistoryId = new HistoryId(store.DatabaseId);
            researchEvents.TryPublish(
                logicalEventId => new ResearchEvent(
                    logicalEventId,
                    logicalEventId,
                    ResearchEventKind.RecoveryStarted,
                    mainHistoryId,
                    parentHistoryId: null,
                    researchOperationId,
                    transactionId: null,
                    ["catalog", "main"],
                    ResearchDurabilityPhase.None,
                    authorityGeneration: 0,
                    dependencyEventIds: [],
                    logicalKeyId: null,
                    versionId: null,
                    offset: null,
                    bytes: null),
                out var recoveryStartedEventId);
            HistoryCheckpoint? mainCheckpoint = null;
            var checkpointPath = Path.Combine(fullDirectory, PersistentHistoryCheckpoint.FileName);
            if (store.HasFormatFlag(DatabaseHeader.HistoryCheckpointInitializedFlag))
            {
                mainCheckpoint = PersistentHistoryCheckpoint.TryLoad(
                    fullDirectory,
                    store.DatabaseId,
                    mainHistoryId)
                    ?? throw new StorageCorruptionException("Main history checkpoint is required but missing.");
            }
            else if (File.Exists(checkpointPath) || File.Exists(checkpointPath + ".previous"))
            {
                // A checkpoint written before its capability flag was published is not
                // recovery authority; the unreset WAL still contains complete history.
                TryDeleteFile(checkpointPath);
                TryDeleteFile(checkpointPath + ".previous");
            }

            if (mainCheckpoint is not null)
            {
                RecoveredLogicalHistoryValidator.ValidateCheckpoint(
                    mainCheckpoint,
                    validatedOptions,
                    "Main history checkpoint");
            }

            var checkpointTransactionIds = mainCheckpoint?.Versions
                .Select(version => version.TransactionId)
                .ToHashSet();
            var recovery = WalRecovery.Reconcile(
                store,
                wal,
                mainCheckpoint?.CheckpointSequence,
                checkpointTransactionIds);
            versions = new CommittedVersionStore(new SynchronizedVersionIndex());
            if (mainCheckpoint is not null)
            {
                ReplayHistoryCheckpoint(mainCheckpoint, versions);
            }
            foreach (var transaction in recovery.CommittedTransactions.OrderBy(entry => entry.CommitLsn))
            {
                versions.ReplayCommitted(
                    transaction.TransactionId,
                    transaction.CommitSequence,
                    transaction.Mutations);
            }

            var currentCommitSequence = recovery.CurrentCommitSequence;
            var legacyCurrentState = store
                .SnapshotCurrentState()
                .Where(mutation => !versions.TryGetLatestCommitSequence(mutation.Key, out _))
                .ToArray();
            if (legacyCurrentState.Length != 0)
            {
                if (walWasInitialized)
                {
                    throw new StorageCorruptionException(
                        "Physical current state contains keys that are not represented by the initialized WAL history.");
                }

                // Pre-MVCC physical keys have no stable historical sequence. Persist one
                // synthetic bootstrap transaction in WAL exactly once so their boundary
                // cannot drift forward on later reopens. The first snapshot-store floor is
                // then established at this durable upgrade boundary.
                currentCommitSequence = PersistLegacyBootstrap(
                    store,
                    wal,
                    versions,
                    currentCommitSequence,
                    legacyCurrentState);
            }

            ValidateMainPhysicalState(store, versions);

            // The capability flag is the durable statement that future opens may require
            // WAL-backed logical history. Publish it only after recovery/bootstrap succeeds.
            store.EnsureFormatFlags(DatabaseHeader.WalInitializedFlag);

            // On first v0.5 open, an upgraded database conservatively retains history only
            // from the current validated boundary. A fresh database starts at sequence zero.
            RequireInitializedFileIfFlagged(
                store,
                DatabaseHeader.SnapshotStoreInitializedFlag,
                Path.Combine(fullDirectory, PersistentSnapshotStore.FileName),
                "persistent snapshot metadata");
            snapshotStore = PersistentSnapshotStore.Open(
                fullDirectory,
                store.DatabaseId,
                initialRetentionFloor: currentCommitSequence,
                validatedOptions.FaultInjector);
            store.EnsureFormatFlags(DatabaseHeader.SnapshotStoreInitializedFlag);
            if (snapshotStore.Header.RetentionFloor > currentCommitSequence)
            {
                throw new StorageCorruptionException(
                    "Snapshot retention metadata is newer than recovered committed history.");
            }

            if (snapshotStore.MaximumReferencedSequence > currentCommitSequence)
            {
                throw new StorageCorruptionException(
                    "Snapshot lifecycle metadata references history newer than the recovered database.");
            }

            RequireInitializedFileIfFlagged(
                store,
                DatabaseHeader.HistoryRootStoreInitializedFlag,
                Path.Combine(fullDirectory, PersistentHistoryRootStore.FileName),
                "persistent history-root metadata");
            historyRootStore = PersistentHistoryRootStore.Open(
                fullDirectory,
                store.DatabaseId,
                mainHistoryId,
                validatedOptions.FaultInjector);

            RequireInitializedFileIfFlagged(
                store,
                DatabaseHeader.BranchStoreInitializedFlag,
                Path.Combine(fullDirectory, PersistentBranchMetadataStore.FileName),
                "persistent branch metadata");
            branchStore = PersistentBranchMetadataStore.Open(
                fullDirectory,
                store.DatabaseId,
                mainHistoryId,
                validatedOptions.FaultInjector);

            // Any creation that never reached Active is not externally valid. Resolve it
            // before reconstructing the retention graph so an interrupted create cannot
            // pin arbitrary parent history indefinitely.
            ReconcileIncompleteBranchCreations(fullDirectory, branchStore, historyRootStore);
            ReconcileIncompleteBranchDeletions(branchStore, historyRootStore);
            var branchDefinitions = branchStore.ListActive()
                .Select(record => ToBranchDefinition(record, store.DatabaseId))
                .ToArray();
            ValidateBranchGraph(branchDefinitions, mainHistoryId, currentCommitSequence);
            ReconcileBranchBaseRoots(historyRootStore, branchDefinitions, store.DatabaseId);

            var snapshotRecords = snapshotStore.ListActive();
            ReconcileSnapshotRoots(historyRootStore, snapshotRecords, store.DatabaseId, mainHistoryId);

            // v1.0 deliberately favors eager correctness validation over lazy branch open: every
            // active branch and its persistent snapshots are reconstructed before the
            // database is exposed as ready.
            branchRuntimes = OpenBranchRuntimes(
                fullDirectory,
                branchDefinitions,
                branchStore,
                historyRootStore,
                validatedOptions,
                store.DatabaseId);
            branchDefinitions = branchRuntimes.Values
                .Select(runtime => runtime.Definition)
                .OrderBy(branch => branch.Depth)
                .ThenBy(branch => branch.Name, StringComparer.Ordinal)
                .ToArray();
            ValidateBranchGraph(branchDefinitions, mainHistoryId, currentCommitSequence);

            SnapshotCatalog snapshots;
            HistoryRootRegistry historyRoots;
            BranchCatalog branches;
            try
            {
                var mainHistoryFloor = mainCheckpoint?.RetentionFloor ?? snapshotStore.Header.RetentionFloor;
                snapshots = new SnapshotCatalog(
                    mainHistoryFloor,
                    currentCommitSequence,
                    snapshotRecords.Select(ToSnapshotDefinition));
                branches = new BranchCatalog(branchDefinitions);
                historyRoots = new HistoryRootRegistry(
                    mainHistoryId,
                    mainHistoryFloor,
                    historyRootStore.ListRetaining().Select(ToHistoryRoot));
                foreach (var branch in branchDefinitions)
                {
                    var branchFloor = branchRuntimes[branch.BranchId].HistoryFloor;
                    historyRoots.RegisterHistory(branch.HistoryId, branchFloor);
                }
                ValidateRecoveredHistoryRoots(
                    historyRoots,
                    branches,
                    mainHistoryId,
                    currentCommitSequence,
                    store.DatabaseId);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or StorageFormatException)
            {
                throw new StorageCorruptionException(
                    "Persistent historical-root or branch metadata is inconsistent with recovered history.",
                    exception);
            }

            store.EnsureFormatFlags(
                DatabaseHeader.HistoryRootStoreInitializedFlag | DatabaseHeader.BranchStoreInitializedFlag);

            var counters = new EngineCounters();
            counters.RecoveryReplayed(recovery.CommittedTransactionCount);
            var database = new ChronicleDatabase(
                store,
                wal,
                snapshotStore,
                historyRootStore,
                snapshots,
                historyRoots,
                branchStore,
                branches,
                branchRuntimes,
                fullDirectory,
                validatedOptions,
                versions,
                currentCommitSequence,
                faultInjector,
                counters,
                researchEvents,
                researchOperationId);

            var historyReadyEventId = database.PublishResearchEvent(
                ResearchEventKind.HistoryReady,
                ResearchDurabilityPhase.AuthorityPublished,
                ["catalog", "history-roots", "main"],
                recoveryStartedEventId > 0 ? [recoveryStartedEventId] : []);
            database.PublishResearchEvent(
                ResearchEventKind.RecoveryCompleted,
                ResearchDurabilityPhase.AuthorityPublished,
                ["catalog", "main"],
                historyReadyEventId > 0
                    ? [historyReadyEventId]
                    : recoveryStartedEventId > 0 ? [recoveryStartedEventId] : []);
            return database;
        }
        catch
        {
            if (branchRuntimes is not null)
            {
                foreach (var runtime in branchRuntimes.Values)
                {
                    runtime.Dispose();
                }
            }

            try
            {
                branchStore?.Dispose();
            }
            finally
            {
                try
                {
                    historyRootStore?.Dispose();
                }
                finally
                {
                    try
                    {
                        snapshotStore?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            wal?.Dispose();
                        }
                        finally
                        {
                            try
                            {
                                store.Dispose();
                            }
                            finally
                            {
                                versions?.Dispose();
                            }
                        }
                    }
                }
            }

            throw;
        }
    }

    public ChronicleTransaction BeginTransaction()
    {
        EnterOperation();
        try
        {
            // Transaction start and process-local retention registration are one
            // history-lifecycle operation. Otherwise GC could advance the generic
            // floor after StartSequence is sampled but before that boundary becomes
            // visible to the retention planner.
            lock (_historyGate)
            {
                var transaction = new Transaction(
                    startSequence: GetCurrentCommitSequence(),
                    historyId: _mainHistoryId);
                transaction.Begin();
                var boundaryToken = _activeHistoryBoundaries.Register(
                    _mainHistoryId,
                    transaction.StartSequence);
                _counters.TransactionStarted();
                return new ChronicleTransaction(new MainTransactionHost(this, boundaryToken), transaction);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        using var transaction = BeginTransaction();
        transaction.Put(key, value);
        transaction.Commit();
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        EnterOperation();
        try
        {
            return _versions.TryReadLatest(new BinaryKey(key), out value);
        }
        finally
        {
            ExitOperation();
        }
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        using var transaction = BeginTransaction();
        var existed = transaction.TryGet(key, out _);
        transaction.Delete(key);
        transaction.Commit();
        return existed;
    }

    public ChronicleSnapshot CreateSnapshot(string name)
    {
        EnterOperation();
        var started = Stopwatch.GetTimestamp();
        try
        {
            lock (_historyGate)
            {
                var boundary = GetCurrentCommitSequence();
                SnapshotDefinition definition;
                try
                {
                    definition = _snapshots.PrepareCreate(name, boundary);
                }
                catch (InvalidOperationException)
                {
                    throw new SnapshotNameConflictException(name);
                }

                var metadataAppended = false;
                try
                {
                    var root = ToHistoryRoot(definition, _databaseId, _mainHistoryId);
                    var rootRecord = ToHistoryRootStoreRecord(definition, _databaseId, _mainHistoryId);
                    _snapshotStore.AppendCreate(
                        definition.SnapshotId,
                        definition.Sequence,
                        definition.CreatedUnixMilliseconds,
                        definition.Name);
                    metadataAppended = true;
                    _historyRootStore.AppendCreate(rootRecord);
                    _historyRoots.RegisterActive(root);
                    _snapshots.RegisterPersisted(definition, GetCurrentCommitSequence());
                }
                catch
                {
                    if (_snapshotStore.IsFaulted || _historyRootStore.IsFaulted || metadataAppended)
                    {
                        MarkFaulted();
                    }

                    throw;
                }

                _counters.SnapshotCreated(Stopwatch.GetTimestamp() - started);
                var boundaryToken = _activeHistoryBoundaries.Register(_mainHistoryId, definition.Sequence);
                return new ChronicleSnapshot(this, ToSnapshotInfo(definition), boundaryToken);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public IReadOnlyList<ChronicleSnapshotInfo> ListSnapshots()
    {
        EnterOperation();
        try
        {
            return _snapshots.List().Select(ToSnapshotInfo).ToArray();
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleSnapshot OpenSnapshot(Guid snapshotId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var id = new SnapshotId(snapshotId);
                if (!id.IsValid || !_snapshots.TryGet(id, out var definition) || definition is null)
                {
                    throw new SnapshotNotFoundException(snapshotId.ToString());
                }

                var boundaryToken = _activeHistoryBoundaries.Register(_mainHistoryId, definition.Sequence);
                return new ChronicleSnapshot(this, ToSnapshotInfo(definition), boundaryToken);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleSnapshot OpenSnapshot(string name)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                if (!_snapshots.TryGet(name, out var definition) || definition is null)
                {
                    throw new SnapshotNotFoundException($"named '{name}'");
                }

                var boundaryToken = _activeHistoryBoundaries.Register(_mainHistoryId, definition.Sequence);
                return new ChronicleSnapshot(this, ToSnapshotInfo(definition), boundaryToken);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public void DeleteSnapshot(Guid snapshotId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var id = new SnapshotId(snapshotId);
                if (!id.IsValid || !_snapshots.TryGet(id, out var definition) || definition is null)
                {
                    throw new SnapshotNotFoundException(snapshotId.ToString());
                }

                var rootId = new HistoryRootId(id.Value);
                var deletionStarted = false;
                var metadataAppended = false;
                try
                {
                    _historyRoots.BeginDelete(rootId);
                    deletionStarted = true;
                    _snapshotStore.AppendDelete(id);
                    metadataAppended = true;
                    _historyRootStore.AppendDelete(rootId);
                    _snapshots.RemoveRequired(id);
                    _historyRoots.CompleteDelete(rootId);
                }
                catch
                {
                    if (deletionStarted && !metadataAppended)
                    {
                        _historyRoots.CancelDelete(rootId);
                    }

                    if (_snapshotStore.IsFaulted || _historyRootStore.IsFaulted || metadataAppended)
                    {
                        MarkFaulted();
                    }

                    throw;
                }
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleHistoricalView OpenHistoricalView(ulong sequence)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var boundary = new CommitSequence(sequence);
                ValidateHistoricalBoundary(boundary);
                var boundaryToken = _activeHistoryBoundaries.Register(_mainHistoryId, boundary);
                return new ChronicleHistoricalView(this, sequence, boundaryToken);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleDatabaseDiagnostics GetDiagnostics()
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var current = GetCurrentCommitSequence();
                var counters = _counters.Snapshot();
                var versions = _versions.GetStatistics();
                var wal = _wal.GetStatistics();
                var snapshots = _snapshots.List();
                var runtimes = _branchRuntimes.Values.ToArray();
                var branchWalBytes = runtimes.Sum(runtime => runtime.Wal.GetStatistics().FileLength);
                var checkpointBytes = GetFileLengthIfExists(
                    Path.Combine(_databaseDirectory, PersistentHistoryCheckpoint.FileName))
                    + runtimes.Sum(runtime => GetFileLengthIfExists(
                        Path.Combine(runtime.Directory, PersistentHistoryCheckpoint.FileName)));
                var averageWalFlushMilliseconds = wal.FlushCount == 0
                    ? 0
                    : wal.TotalFlushStopwatchTicks * 1000d / Stopwatch.Frequency / wal.FlushCount;
                return new ChronicleDatabaseDiagnostics(
                    DatabaseId: _databaseId,
                    State: State,
                    CurrentCommitSequence: current.Value,
                    RetentionFloor: GetHistoryRetentionFloor().Value,
                    CurrentKeyCount: versions.CurrentKeyCount,
                    ActiveTransactions: counters.ActiveTransactions,
                    CommitAttempts: counters.CommitAttempts,
                    SuccessfulCommits: counters.SuccessfulCommits,
                    Aborts: counters.Aborts,
                    ConflictAborts: counters.ConflictAborts,
                    CommitSerializationContention: counters.CommitSerializationContention,
                    AverageCommitMilliseconds: counters.AverageCommitMilliseconds,
                    VersionCount: versions.VersionCount,
                    VersionChainCount: versions.ChainCount,
                    AverageVersionChainLength: versions.AverageChainLength,
                    MaximumVersionChainLength: versions.MaximumChainLength,
                    IndexContention: versions.Index.ContendedAcquisitions,
                    NextWalLsn: wal.NextLsn,
                    WalFileBytes: wal.FileLength,
                    WalBytesWrittenThisSession: wal.BytesWrittenThisSession,
                    WalFlushCount: wal.FlushCount,
                    AverageWalFlushMilliseconds: averageWalFlushMilliseconds,
                    RecoveryReplayedTransactions: counters.RecoveryReplayedTransactions,
                    SnapshotCount: snapshots.Count,
                    RetainingRootCount: _historyRoots.Count,
                    OldestSnapshotSequence: snapshots.Count == 0 ? null : snapshots.Min(item => item.Sequence.Value),
                    NewestSnapshotSequence: snapshots.Count == 0 ? null : snapshots.Max(item => item.Sequence.Value),
                    AverageSnapshotCreateMilliseconds: counters.AverageSnapshotCreateMilliseconds,
                    SnapshotMetadataBytes: _snapshotStore.FileLength,
                    DataFileBytes: _store.DataLength,
                    DataPageCount: _store.PageCount,
                    OverflowPageCount: _store.OverflowPageCount,
                    BranchCount: _branches.Count,
                    BranchMetadataBytes: _branchStore.FileLength,
                    BranchLocalDataBytes: runtimes.Sum(runtime => runtime.Store.DataLength),
                    BranchLocalVersionCount: runtimes.Sum(runtime => runtime.Versions.VersionCount),
                    BranchSnapshotCount: runtimes.Sum(runtime => runtime.Snapshots.Count),
                    GarbageCollectionPasses: counters.GarbageCollectionPasses,
                    GarbageCollectionReclaimedVersions: counters.GarbageCollectionReclaimedVersions,
                    GarbageCollectionCheckpointBytes: counters.GarbageCollectionCheckpointBytes,
                    GarbageCollectionMilliseconds: counters.GarbageCollectionMilliseconds,
                    CompactionPasses: counters.CompactionPasses,
                    CompactionBytesRewritten: counters.CompactionBytesRewritten,
                    CompactionBytesReclaimed: counters.CompactionBytesReclaimed,
                    CompactionMilliseconds: counters.CompactionMilliseconds,
                    BranchLocalWalBytes: branchWalBytes,
                    HistoryRootMetadataBytes: _historyRootStore.FileLength,
                    HistoryCheckpointBytes: checkpointBytes);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleHistoryTopologyDiagnostics GetHistoryTopologyDiagnostics()
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var mainStatistics = _versions.GetStatistics();
                var branchActiveTransactions = _branchRuntimes.Values.Sum(runtime => (long)runtime.ActiveTransactions);
                var counters = _counters.Snapshot();
                var mainActiveTransactions = checked((int)Math.Min(
                    int.MaxValue,
                    Math.Max(0L, counters.ActiveTransactions - branchActiveTransactions)));
                var mainRetentionBoundaries = _activeHistoryBoundaries.CountForHistory(_mainHistoryId);
                var mainHistoricalHandles = Math.Max(0, mainRetentionBoundaries - mainActiveTransactions);
                var main = new ChronicleHistoryDiagnostics(
                    HistoryId: _mainHistoryId.Value,
                    Kind: "Main",
                    BranchId: null,
                    Name: "main",
                    ParentHistoryId: null,
                    ParentBaseSequence: null,
                    Depth: 0,
                    CurrentSequence: GetCurrentCommitSequence().Value,
                    RetentionFloor: GetHistoryRetentionFloor().Value,
                    LocalCurrentKeyCount: mainStatistics.CurrentKeyCount,
                    VersionCount: mainStatistics.VersionCount,
                    VersionChainCount: mainStatistics.ChainCount,
                    MaximumVersionChainLength: mainStatistics.MaximumChainLength,
                    SnapshotCount: _snapshots.Count,
                    DataFileBytes: _store.DataLength,
                    WalFileBytes: _wal.GetStatistics().FileLength,
                    OpenRetentionBoundaryCount: mainRetentionBoundaries,
                    OpenBranchHandleCount: 0,
                    ActiveTransactionCount: mainActiveTransactions,
                    OpenHistoricalHandleCount: mainHistoricalHandles);

                var branches = _branchRuntimes.Values
                    .OrderBy(runtime => runtime.Definition.Depth)
                    .ThenBy(runtime => runtime.Definition.Name, StringComparer.Ordinal)
                    .ThenBy(runtime => runtime.Definition.BranchId.Value)
                    .Select(runtime =>
                    {
                        var definition = runtime.Definition;
                        var statistics = runtime.Versions.GetStatistics();
                        return new ChronicleHistoryDiagnostics(
                            HistoryId: definition.HistoryId.Value,
                            Kind: "Branch",
                            BranchId: definition.BranchId.Value,
                            Name: definition.Name,
                            ParentHistoryId: definition.ParentHistoryId.Value,
                            ParentBaseSequence: definition.ParentBaseSequence.Value,
                            Depth: definition.Depth,
                            CurrentSequence: definition.LocalCurrentSequence.Value,
                            RetentionFloor: runtime.HistoryFloor.Value,
                            LocalCurrentKeyCount: statistics.CurrentKeyCount,
                            VersionCount: statistics.VersionCount,
                            VersionChainCount: statistics.ChainCount,
                            MaximumVersionChainLength: statistics.MaximumChainLength,
                            SnapshotCount: runtime.Snapshots.Count,
                            DataFileBytes: runtime.Store.DataLength,
                            WalFileBytes: runtime.Wal.GetStatistics().FileLength,
                            OpenRetentionBoundaryCount: _activeHistoryBoundaries.CountForHistory(definition.HistoryId),
                            OpenBranchHandleCount: runtime.OpenBranchHandles,
                            ActiveTransactionCount: runtime.ActiveTransactions,
                            OpenHistoricalHandleCount: runtime.OpenHistoricalHandles);
                    })
                    .ToArray();

                var roots = _historyRoots.ListActive()
                    .Select(root => new ChronicleRetentionRootDiagnostics(
                        RootId: root.RootId.Value,
                        Kind: root.Kind.ToString(),
                        OwnerHistoryId: root.HistoryId.Value,
                        ProtectedHistoryId: root.ProtectedHistoryId.Value,
                        Boundary: root.Boundary.Value,
                        CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(root.CreatedUnixMilliseconds),
                        State: root.State.ToString()))
                    .ToArray();

                return new ChronicleHistoryTopologyDiagnostics(main, branches, roots);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private static long GetFileLengthIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Flush()
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var runtimes = _branchRuntimes.Values
                    .OrderBy(runtime => runtime.Definition.BranchId.Value)
                    .ToArray();
                var acquired = AcquireMaintenanceCommitGates(runtimes);
                try
                {
                    _store.Flush();
                    _wal.Flush();
                    foreach (var runtime in runtimes)
                    {
                        runtime.Store.Flush();
                        runtime.Wal.Flush();
                    }
                }
                catch
                {
                    MarkFaulted();
                    throw;
                }
                finally
                {
                    ReleaseMaintenanceCommitGates(acquired);
                }
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private long PublishResearchEvent(
        ResearchEventKind eventKind,
        ResearchDurabilityPhase durabilityPhase,
        IReadOnlyList<string> resources,
        IReadOnlyList<long> dependencies)
    {
        return _researchEvents.TryPublish(
                logicalEventId => new ResearchEvent(
                    logicalEventId,
                    logicalEventId,
                    eventKind,
                    _mainHistoryId,
                    parentHistoryId: null,
                    _researchOperationId,
                    transactionId: null,
                    resources,
                    durabilityPhase,
                    authorityGeneration: 0,
                    dependencies,
                    logicalKeyId: null,
                    versionId: null,
                    offset: null,
                    bytes: null),
                out var logicalEventId)
            ? logicalEventId
            : 0;
    }

    public void Dispose()
    {
        _lifecycle.EnterWriteLock();
        try
        {
            lock (_stateGate)
            {
                if (_state == DatabaseState.Closed)
                {
                    return;
                }

                _state = DatabaseState.Closed;
            }

            try
            {
                foreach (var runtime in _branchRuntimes.Values)
                {
                    runtime.Dispose();
                }
            }
            finally
            {
                try
                {
                    _branchStore.Dispose();
                }
                finally
                {
                    try
                    {
                        _historyRootStore.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            _snapshotStore.Dispose();
                        }
                        finally
                        {
                            try
                            {
                                _wal.Dispose();
                            }
                            finally
                            {
                                try
                                {
                                    _store.Dispose();
                                }
                                finally
                                {
                                    _versions.Dispose();
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            _lifecycle.ExitWriteLock();
        }
    }

    internal bool ReadAt(
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
    {
        EnterOperation();
        try
        {
            var current = GetCurrentCommitSequence();
            if (visibilityBoundary > current)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(visibilityBoundary),
                    "A transaction cannot read beyond current committed history.");
            }

            return _versions.TryRead(new BinaryKey(key), visibilityBoundary, out value);
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool ReadHistorical(
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
    {
        EnterOperation();
        try
        {
            ValidateHistoricalBoundary(visibilityBoundary);
            return _versions.TryRead(new BinaryKey(key), visibilityBoundary, out value);
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool ReadPinnedHistorical(
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
    {
        EnterOperation();
        try
        {
            var current = GetCurrentCommitSequence();
            if (visibilityBoundary > current)
            {
                throw new HistoricalStateUnavailableException(
                    visibilityBoundary.Value,
                    GetHistoryRetentionFloor().Value,
                    current.Value);
            }
            return _versions.TryRead(new BinaryKey(key), visibilityBoundary, out value);
        }
        finally
        {
            ExitOperation();
        }
    }

    internal void Commit(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnterOperation();
        var started = Stopwatch.GetTimestamp();
        _counters.CommitAttempted();
        try
        {
            EnterCommitSerialization();
            try
            {
                ThrowIfUsable();
                if (transaction.HistoryId != _mainHistoryId)
                {
                    throw new InvalidOperationException(
                        "A transaction may only be committed through the history domain that created it.");
                }

                var writes = transaction.PrepareAndGetWriteSet();
                List<StorageMutation> mutations;
                List<(WalRecordType Type, byte[] Payload)> walPayloads;
                CommitSequence commitSequence;
                byte[] commitPayload;

                try
                {
                    ValidateWriteConflicts(transaction, writes);
                    commitSequence = NextCommitSequence();

                    mutations = new List<StorageMutation>(writes.Count);
                    walPayloads = new List<(WalRecordType Type, byte[] Payload)>(writes.Count);
                    foreach (var write in writes)
                    {
                        if (write.IsDelete)
                        {
                            var payload = WalMutationCodec.EncodeDelete(write.Key);
                            ValidateWalPayload(payload);
                            walPayloads.Add((WalRecordType.Delete, payload));
                            mutations.Add(new StorageMutation(write.Key, isDelete: true, ReadOnlySpan<byte>.Empty));
                        }
                        else
                        {
                            var payload = WalMutationCodec.EncodePut(write.Key, write.Value.Span);
                            ValidateWalPayload(payload);
                            walPayloads.Add((WalRecordType.Put, payload));
                            mutations.Add(new StorageMutation(write.Key, isDelete: false, write.Value.Span));
                        }
                    }

                    // Nothing deterministic is permitted to reject the transaction after
                    // the Commit record becomes durable.
                    _store.ValidateBatch(mutations);
                    _versions.ValidatePublicationCapacity(writes);
                    ValidateWalCapacity(walPayloads.Count + 2);
                    commitPayload = WalCommitCodec.Encode(commitSequence, _store.DataLength);
                }
                catch (TransactionConflictException)
                {
                    if (transaction.State == TransactionState.Preparing)
                    {
                        transaction.Abort();
                    }

                    _counters.ConflictAbortRecorded();
                    throw;
                }
                catch
                {
                    if (transaction.State == TransactionState.Preparing)
                    {
                        transaction.Abort();
                        _counters.AbortRecorded();
                    }

                    throw;
                }

                var walTouched = false;
                try
                {
                    _faultInjector?.Hit(TransactionFaultPoint.BeforeWalAppend);
                    walTouched = true;
                    _wal.Append(WalRecordType.Begin, transaction.TransactionId, []);
                    foreach (var (type, payload) in walPayloads)
                    {
                        _wal.Append(type, transaction.TransactionId, payload);
                    }

                    transaction.MarkCommitting();
                    _wal.Append(WalRecordType.Commit, transaction.TransactionId, commitPayload);
                    _faultInjector?.Hit(TransactionFaultPoint.AfterWalAppend);
                    _faultInjector?.Hit(TransactionFaultPoint.BeforeWalFlush);
                    _wal.Flush();
                    transaction.MarkDurableCommitted(commitSequence);
                    _faultInjector?.Hit(TransactionFaultPoint.AfterWalFlush);
                    _faultInjector?.Hit(TransactionFaultPoint.BeforePhysicalPublication);
                    _store.ApplyBatch(mutations);
                    _faultInjector?.Hit(TransactionFaultPoint.AfterPhysicalPublication);

                    // Multi-key logical publication is one writer-critical section inside
                    // the version store. Readers either observe the prior boundary or the
                    // complete new transaction; current sequence is published only after it.
                    _versions.PublishCommitted(transaction.TransactionId, commitSequence, writes);
                    transaction.MarkCommitted();
                    PublishCurrentCommitSequence(commitSequence);
                    _counters.CommitSucceeded(Stopwatch.GetTimestamp() - started);
                    _faultInjector?.Hit(TransactionFaultPoint.BeforeAcknowledgement);
                }
                catch
                {
                    if (walTouched)
                    {
                        MarkFaulted();
                        if (transaction.State is TransactionState.Preparing or TransactionState.Committing)
                        {
                            _wal.MarkFaultedAfterUncertainWrite();
                            transaction.MarkIndeterminate();
                        }
                    }
                    else if (transaction.State == TransactionState.Preparing)
                    {
                        transaction.Abort();
                        _counters.AbortRecorded();
                    }

                    throw;
                }
            }
            finally
            {
                Monitor.Exit(_commitGate);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal void Abort(Transaction transaction, bool throwIfNotAbortable)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Aborting a transaction that has not crossed the WAL commit point only
        // changes its private descriptor. It must not wait behind another writer's
        // durability pause; Transaction's own lock serializes this handle with Commit.
        var state = transaction.State;
        if (state is TransactionState.Created or TransactionState.Active or TransactionState.Preparing)
        {
            transaction.Abort();
            _counters.AbortRecorded();
            return;
        }

        if (throwIfNotAbortable)
        {
            throw new InvalidOperationException(
                $"Transaction {transaction.TransactionId.Value} cannot be aborted from {state}.");
        }
    }

    internal void TransactionHandleCompleted(long boundaryToken)
    {
        _activeHistoryBoundaries.Release(boundaryToken);
        _counters.TransactionFinished();
    }

    internal void HistoricalHandleClosed(long boundaryToken)
        => _activeHistoryBoundaries.Release(boundaryToken);

    private void ValidateWriteConflicts(
        Transaction transaction,
        IReadOnlyList<Transactions.Writes.TransactionWrite> writes)
    {
        foreach (var write in writes)
        {
            if (_versions.TryGetLatestCommitSequence(write.Key, out var latest)
                && latest > transaction.StartSequence)
            {
                throw new TransactionConflictException(
                    transaction.TransactionId.Value,
                    transaction.StartSequence.Value,
                    latest.Value);
            }
        }
    }

    private CommitSequence NextCommitSequence()
    {
        try
        {
            return GetCurrentCommitSequence().Next();
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The commit-sequence space is exhausted.", exception);
        }
    }

    private CommitSequence GetCurrentCommitSequence()
    {
        lock (_stateGate)
        {
            return _currentCommitSequence;
        }
    }

    private void PublishCurrentCommitSequence(CommitSequence sequence)
    {
        lock (_stateGate)
        {
            if (sequence <= _currentCommitSequence)
            {
                throw new InvalidOperationException("Commit publication attempted to move history backwards.");
            }

            _currentCommitSequence = sequence;
        }
    }

    private void ValidateHistoricalBoundary(CommitSequence boundary)
    {
        var current = GetCurrentCommitSequence();
        var retentionFloor = GetHistoryRetentionFloor();
        if (boundary < retentionFloor || boundary > current)
        {
            throw new HistoricalStateUnavailableException(
                boundary.Value,
                retentionFloor.Value,
                current.Value);
        }
    }

    private CommitSequence GetHistoryRetentionFloor()
        => _historyRoots.GetHistoryFloor(_mainHistoryId)
            ?? throw new InvalidOperationException("The main history must always have a retention floor.");

    private static void ReplayHistoryCheckpoint(
        HistoryCheckpoint checkpoint,
        CommittedVersionStore versions)
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file is not authoritative until its capability flag is published.
            // Cleanup failure does not change the WAL/checkpoint recovery decision.
        }
    }

    private void EnterOperation()
    {
        _lifecycle.EnterReadLock();
        try
        {
            ThrowIfUsable();
        }
        catch
        {
            _lifecycle.ExitReadLock();
            throw;
        }
    }

    private void ExitOperation() => _lifecycle.ExitReadLock();

    private void EnterCommitSerialization()
    {
        if (Monitor.TryEnter(_commitGate))
        {
            return;
        }

        _counters.CommitSerializationContended();
        Monitor.Enter(_commitGate);
    }

    private void ThrowIfUsable()
    {
        lock (_stateGate)
        {
            if (_state == DatabaseState.Closed)
            {
                ObjectDisposedException.ThrowIf(true, this);
            }

            if (_state == DatabaseState.Faulted)
            {
                throw new ChronicleDatabaseFaultedException();
            }
        }
    }

    private void MarkFaulted()
    {
        lock (_stateGate)
        {
            if (_state == DatabaseState.Open)
            {
                _state = DatabaseState.Faulted;
            }
        }
    }

    private static SnapshotDefinition ToSnapshotDefinition(SnapshotStoreRecord record)
        => new(
            record.SnapshotId,
            record.Name,
            record.Sequence,
            record.CreatedUnixMilliseconds);

    private static HistoryRoot ToHistoryRoot(HistoryRootStoreRecord record)
    {
        if (record.RootState != (byte)HistoryRootState.Active
            || !Enum.IsDefined((HistoryRootKind)record.RootKind))
        {
            throw new StorageFormatException("Persistent history-root metadata contains an unsupported active root.");
        }

        var kind = (HistoryRootKind)record.RootKind;
        if (kind == HistoryRootKind.BranchBase)
        {
            if (!record.ParentHistoryId.IsValid || record.ParentHistoryId == record.HistoryId)
            {
                throw new StorageFormatException("Branch-base history root has invalid parent identity.");
            }
        }
        else if (record.ParentHistoryId.IsValid)
        {
            throw new StorageFormatException("Only branch-base history roots may carry a parent history.");
        }

        return new HistoryRoot(
            record.RootId,
            kind,
            record.OwnerDatabaseId,
            record.HistoryId,
            record.ParentHistoryId,
            record.Boundary,
            record.CreatedUnixMilliseconds,
            HistoryRootState.Active);
    }

    private static HistoryRoot ToHistoryRoot(
        SnapshotDefinition definition,
        Guid databaseId,
        HistoryId mainHistoryId)
        => new(
            new HistoryRootId(definition.SnapshotId.Value),
            HistoryRootKind.PersistentSnapshot,
            databaseId,
            mainHistoryId,
            HistoryId.Empty,
            definition.Sequence,
            definition.CreatedUnixMilliseconds,
            HistoryRootState.Active);

    private static HistoryRootStoreRecord ToHistoryRootStoreRecord(
        SnapshotDefinition definition,
        Guid databaseId,
        HistoryId mainHistoryId)
        => new(
            HistoryRootStoreRecordType.Create,
            EventSequence: 0,
            new HistoryRootId(definition.SnapshotId.Value),
            (byte)HistoryRootKind.PersistentSnapshot,
            (byte)HistoryRootState.Active,
            databaseId,
            mainHistoryId,
            HistoryId.Empty,
            definition.Sequence,
            definition.CreatedUnixMilliseconds);

    private static void ReconcileSnapshotRoots(
        PersistentHistoryRootStore historyRootStore,
        IReadOnlyList<SnapshotStoreRecord> snapshots,
        Guid databaseId,
        HistoryId mainHistoryId)
    {
        ArgumentNullException.ThrowIfNull(historyRootStore);
        ArgumentNullException.ThrowIfNull(snapshots);

        var snapshotRootIds = new HashSet<HistoryRootId>();
        foreach (var snapshot in snapshots)
        {
            var definition = ToSnapshotDefinition(snapshot);
            var expected = ToHistoryRootStoreRecord(definition, databaseId, mainHistoryId);
            snapshotRootIds.Add(expected.RootId);
            if (!historyRootStore.TryGet(expected.RootId, out var existing) || existing is null)
            {
                historyRootStore.AppendCreate(expected);
                continue;
            }

            if (existing.RootState == (byte)HistoryRootState.Deleted
                || existing.RootKind != expected.RootKind
                || existing.OwnerDatabaseId != expected.OwnerDatabaseId
                || existing.HistoryId != expected.HistoryId
                || existing.ParentHistoryId != expected.ParentHistoryId
                || existing.Boundary != expected.Boundary
                || existing.CreatedUnixMilliseconds != expected.CreatedUnixMilliseconds)
            {
                throw new StorageCorruptionException(
                    $"History-root metadata for snapshot {definition.SnapshotId.Value} does not match the snapshot catalog.");
            }
        }

        foreach (var root in historyRootStore.ListRetaining())
        {
            if (root.RootKind == (byte)HistoryRootKind.PersistentSnapshot
                && root.HistoryId == mainHistoryId
                && !snapshotRootIds.Contains(root.RootId))
            {
                historyRootStore.AppendDelete(root.RootId);
            }
        }
    }

    private ChronicleSnapshotInfo ToSnapshotInfo(SnapshotDefinition definition)
        => new(
            definition.SnapshotId.Value,
            _databaseId,
            definition.Name,
            definition.Sequence.Value,
            DateTimeOffset.FromUnixTimeMilliseconds(definition.CreatedUnixMilliseconds));

    private static void ValidateMainPhysicalState(
        PersistentKeyValueStore store,
        CommittedVersionStore versions)
    {
        var expected = new Dictionary<BinaryKey, byte[]>();
        foreach (var group in versions.SnapshotHistory().GroupBy(version => version.Key))
        {
            var latest = group.OrderBy(version => version.CommitSequence.Value).Last();
            if (!latest.IsDelete)
            {
                expected[latest.Key] = latest.Value.ToArray();
            }
        }

        var actual = store.SnapshotCurrentState();
        if (actual.Count != expected.Count)
        {
            throw new StorageCorruptionException(
                "Physical Main current state does not match recovered MVCC history.");
        }

        foreach (var mutation in actual)
        {
            if (mutation.IsDelete
                || !expected.TryGetValue(mutation.Key, out var expectedValue)
                || !expectedValue.AsSpan().SequenceEqual(mutation.Value.Span))
            {
                throw new StorageCorruptionException(
                    "Physical Main current state does not match recovered MVCC history.");
            }
        }
    }

    private static CommitSequence PersistLegacyBootstrap(
        PersistentKeyValueStore store,
        WalLog wal,
        CommittedVersionStore versions,
        CommitSequence currentSequence,
        StorageMutation[] legacyCurrentState)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(legacyCurrentState);
        if (legacyCurrentState.Length == 0)
        {
            return currentSequence;
        }

        CommitSequence bootstrapSequence;
        try
        {
            bootstrapSequence = currentSequence.Next();
        }
        catch (OverflowException exception)
        {
            throw new StorageLimitException(
                "Legacy state cannot be assigned a durable upgrade sequence because commit-sequence space is exhausted.",
                exception);
        }

        store.ValidateBatch(legacyCurrentState);
        var payloads = new List<byte[]>(legacyCurrentState.Length);
        foreach (var mutation in legacyCurrentState)
        {
            if (mutation.IsDelete)
            {
                throw new StorageCorruptionException(
                    "The physical current-state bootstrap unexpectedly contained a tombstone.");
            }

            var payload = WalMutationCodec.EncodePut(mutation.Key, mutation.Value.Span);
            ValidateWalPayload(payload);
            payloads.Add(payload);
        }

        var requiredRecords = checked((ulong)payloads.Count + 2UL);
        if (wal.NextLsn > ulong.MaxValue - requiredRecords)
        {
            throw new ChronicleDB.Wal.Errors.WalLimitException(
                "The WAL LSN space cannot fit the legacy-state bootstrap transaction.");
        }

        versions.ValidateReplayCapacity(legacyCurrentState);
        var transactionId = TransactionId.New();
        var commitPayload = WalCommitCodec.Encode(bootstrapSequence, store.DataLength);
        wal.Append(WalRecordType.Begin, transactionId, []);
        foreach (var payload in payloads)
        {
            wal.Append(WalRecordType.Put, transactionId, payload);
        }

        wal.Append(WalRecordType.Commit, transactionId, commitPayload);
        wal.Flush();
        versions.ReplayCommitted(transactionId, bootstrapSequence, legacyCurrentState);
        return bootstrapSequence;
    }

    private static void RequireInitializedFileIfFlagged(
        PersistentKeyValueStore store,
        uint formatFlag,
        string path,
        string description)
    {
        if (store.HasFormatFlag(formatFlag) && !File.Exists(path))
        {
            throw new StorageCorruptionException(
                $"Database metadata requires {description}, but '{Path.GetFileName(path)}' is missing.");
        }
    }

    private static void ValidateWalPayload(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > WalMutationCodec.MaxRecordPayloadSize)
        {
            throw new ChronicleDB.Wal.Errors.WalLimitException(
                "The mutation does not fit in the maximum WAL record payload.");
        }
    }

    private void ValidateWalCapacity(int recordCount)
        => ValidateWalCapacity(_wal, recordCount);

    private static void ValidateWalCapacity(WalLog wal, int recordCount)
    {
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordCount);
        var required = checked((ulong)recordCount);
        var nextLsn = wal.NextLsn;
        if (nextLsn > ulong.MaxValue - required)
        {
            throw new ChronicleDB.Wal.Errors.WalLimitException(
                "The WAL LSN space cannot fit the complete transaction.");
        }
    }
}
