using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.History.Roots;
using ChronicleDB.Maintenance;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.History;
using ChronicleDB.Transactions.Mvcc;

namespace ChronicleDB;

public sealed partial class ChronicleDatabase
{
    /// <summary>
    /// Advances generic historical floors, persists an equivalent retained-history
    /// checkpoint for each processed history, rotates the corresponding WAL only
    /// after that checkpoint is durable, then removes logically unreachable managed
    /// MVCC versions. Explicit roots and open readers remain protected independently.
    /// </summary>
    public GarbageCollectionResult RunGarbageCollection(GarbageCollectionOptions? options = null)
    {
        options ??= new GarbageCollectionOptions();
        options.Validate();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var runtimes = options.IncludeBranches
                    ? _branchRuntimes.Values.OrderBy(runtime => runtime.Definition.BranchId.Value).ToArray()
                    : [];
                var acquired = AcquireMaintenanceCommitGates(runtimes);
                try
                {
                    var historiesProcessed = 0;
                    var reclaimedVersions = 0;
                    long checkpointBytes = 0;

                    var mainCurrent = GetCurrentCommitSequence();
                    var mainCurrentFloor = GetHistoryRetentionFloor();
                    var mainTargetFloor = ComputeTargetFloor(mainCurrentFloor, mainCurrent, options.RetainRecentCommits);
                    var mainPins = GetPinnedBoundaries(_mainHistoryId);
                    var mainProjection = _versions.CreateRetentionProjection(mainTargetFloor, mainPins);
                    var mainFloorAdvances = mainTargetFloor > mainCurrentFloor;
                    var mainProjectionShrinks = mainProjection.Count < _versions.VersionCount;
                    if (mainFloorAdvances || mainProjectionShrinks)
                    {
                        checkpointBytes = checked(checkpointBytes + PublishMainHistoryCheckpoint(
                            mainCurrent,
                            mainTargetFloor,
                            mainProjection));
                        var compacted = _versions.CompactHistory(mainTargetFloor, mainPins);
                        reclaimedVersions = checked(reclaimedVersions + compacted.ReclaimedVersions);
                        if (mainFloorAdvances)
                        {
                            _historyRoots.AdvanceHistoryFloor(_mainHistoryId, mainTargetFloor);
                            _snapshots.AdvanceRetentionFloor(mainTargetFloor, mainCurrent);
                        }
                        historiesProcessed++;
                    }

                    foreach (var runtime in runtimes)
                    {
                        var current = runtime.Definition.LocalCurrentSequence;
                        var currentFloor = runtime.HistoryFloor;
                        var targetFloor = ComputeTargetFloor(currentFloor, current, options.RetainRecentCommits);
                        var pins = GetPinnedBoundaries(runtime.Definition.HistoryId);
                        var projection = runtime.Versions.CreateRetentionProjection(targetFloor, pins);
                        var floorAdvances = targetFloor > currentFloor;
                        var projectionShrinks = projection.Count < runtime.Versions.VersionCount;
                        if (!floorAdvances && !projectionShrinks)
                        {
                            continue;
                        }

                        checkpointBytes = checked(checkpointBytes + PublishBranchHistoryCheckpoint(
                            runtime,
                            current,
                            targetFloor,
                            projection));
                        var compacted = runtime.Versions.CompactHistory(targetFloor, pins);
                        reclaimedVersions = checked(reclaimedVersions + compacted.ReclaimedVersions);
                        if (floorAdvances)
                        {
                            runtime.AdvanceHistoryFloor(targetFloor);
                            _historyRoots.AdvanceHistoryFloor(runtime.Definition.HistoryId, targetFloor);
                        }
                        historiesProcessed++;
                    }

                    var deletedDirectories = ReclaimDeletedBranchDirectories();

                    // Lifecycle journals are append-only during foreground operation.
                    // Once logical/physical reclamation has been published, rewrite only
                    // canonical active state so bounded create/delete workloads do not
                    // leak metadata indefinitely. Transaction history is deliberately not
                    // copied into branch metadata; WAL/checkpoint own that responsibility.
                    _snapshotStore.CompactJournal();
                    foreach (var runtime in runtimes)
                    {
                        runtime.SnapshotStore.CompactJournal();
                    }
                    _historyRootStore.CompactJournal();
                    _historyRoots.PruneDeleted();
                    _branchStore.CompactJournal();

                    var result = new GarbageCollectionResult(
                        historiesProcessed,
                        reclaimedVersions,
                        checkpointBytes,
                        GetHistoryRetentionFloor().Value,
                        deletedDirectories);
                    _counters.GarbageCollectionCompleted(
                        reclaimedVersions,
                        checkpointBytes,
                        System.Diagnostics.Stopwatch.GetTimestamp() - started);
                    return result;
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

    /// <summary>
    /// Rewrites selected physical state through the storage layer's copy-and-publish
    /// protocol. A retained-history checkpoint is first refreshed by GC so recovery
    /// never depends on a physical representation being replaced.
    /// </summary>
    public CompactionResult RunCompaction(CompactionOptions? options = null)
    {
        options ??= new CompactionOptions();
        options.Validate();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

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
                    var candidates = SelectCompactionCandidates(
                        BuildCompactionCandidates(runtimes, options),
                        options)
                        .ToArray();
                    var histories = 0;
                    long before = 0;
                    long after = 0;
                    long rewritten = 0;

                    foreach (var candidate in candidates)
                    {
                        // Physical rewrite changes append-length recovery bases. Refresh
                        // only the history that will actually be rewritten, then rotate
                        // its WAL before publishing any new physical representation.
                        RefreshRecoveryCheckpointForCompaction(candidate.Runtime);

                        StorageRewriteResult rewriteResult;
                        if (candidate.Runtime is null)
                        {
                            rewriteResult = _store.RewriteState(candidate.DesiredState);
                        }
                        else
                        {
                            rewriteResult = candidate.Runtime.Store.RewriteState(candidate.DesiredState);
                            _branchStore.AppendPhysicalBoundary(
                                candidate.Runtime.Definition.BranchId,
                                candidate.Runtime.Store.DataLength);
                        }

                        histories++;
                        before = checked(before + rewriteResult.OldBytes);
                        after = checked(after + rewriteResult.NewBytes);
                        rewritten = checked(rewritten + rewriteResult.NewBytes);
                    }

                    var reclaimed = Math.Max(0, before - after);
                    var result = new CompactionResult(
                        histories,
                        before,
                        after,
                        reclaimed,
                        rewritten);
                    _counters.CompactionCompleted(
                        rewritten,
                        reclaimed,
                        System.Diagnostics.Stopwatch.GetTimestamp() - started);
                    return result;
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

    private void RefreshRecoveryCheckpointForCompaction(BranchRuntime? runtime)
    {
        if (runtime is null)
        {
            var mainCurrent = GetCurrentCommitSequence();
            var mainFloor = GetHistoryRetentionFloor();
            var mainPins = GetPinnedBoundaries(_mainHistoryId);
            var mainProjection = _versions.CreateRetentionProjection(mainFloor, mainPins);
            _ = PublishMainHistoryCheckpoint(mainCurrent, mainFloor, mainProjection);
            return;
        }

        var pins = GetPinnedBoundaries(runtime.Definition.HistoryId);
        var projection = runtime.Versions.CreateRetentionProjection(runtime.HistoryFloor, pins);
        _ = PublishBranchHistoryCheckpoint(
            runtime,
            runtime.Definition.LocalCurrentSequence,
            runtime.HistoryFloor,
            projection);
    }

    private long PublishMainHistoryCheckpoint(
        CommitSequence checkpointSequence,
        CommitSequence floor,
        IReadOnlyList<CommittedVersionSnapshot> versions)
    {
        var checkpoint = ToHistoryCheckpoint(_databaseId, _mainHistoryId, checkpointSequence, floor, versions);
        var bytes = PersistentHistoryCheckpoint.Publish(
            _databaseDirectory,
            checkpoint,
            _storageOptions.FaultInjector);
        _store.EnsureFormatFlags(DatabaseHeader.HistoryCheckpointInitializedFlag);
        _storageOptions.FaultInjector?.Hit(StorageFaultPoint.BeforeHistoryWalReset, PageId.Invalid);
        _wal.ResetToHeader();
        _storageOptions.FaultInjector?.Hit(StorageFaultPoint.AfterHistoryWalReset, PageId.Invalid);
        return bytes;
    }

    private long PublishBranchHistoryCheckpoint(
        BranchRuntime runtime,
        CommitSequence checkpointSequence,
        CommitSequence floor,
        IReadOnlyList<CommittedVersionSnapshot> versions)
    {
        var checkpoint = ToHistoryCheckpoint(
            runtime.Store.DatabaseId,
            runtime.Definition.HistoryId,
            checkpointSequence,
            floor,
            versions);
        var bytes = PersistentHistoryCheckpoint.Publish(
            runtime.Directory,
            checkpoint,
            _storageOptions.FaultInjector);
        runtime.Store.EnsureFormatFlags(DatabaseHeader.HistoryCheckpointInitializedFlag);
        _storageOptions.FaultInjector?.Hit(StorageFaultPoint.BeforeHistoryWalReset, PageId.Invalid);
        runtime.Wal.ResetToHeader();
        _storageOptions.FaultInjector?.Hit(StorageFaultPoint.AfterHistoryWalReset, PageId.Invalid);
        return bytes;
    }

    private CommitSequence[] GetPinnedBoundaries(HistoryId historyId)
    {
        var result = new HashSet<CommitSequence>(_activeHistoryBoundaries.ListBoundaries(historyId));
        foreach (var root in _historyRoots.ListActive(historyId))
        {
            result.Add(root.Boundary);
        }
        return result.OrderBy(item => item.Value).ToArray();
    }

    private static CommitSequence ComputeTargetFloor(
        CommitSequence currentFloor,
        CommitSequence current,
        int retainRecentCommits)
    {
        var retain = (ulong)retainRecentCommits;
        var candidate = current.Value > retain
            ? new CommitSequence(current.Value - retain)
            : CommitSequence.Initial;
        return candidate > currentFloor ? candidate : currentFloor;
    }

    private static HistoryCheckpoint ToHistoryCheckpoint(
        Guid databaseId,
        HistoryId historyId,
        CommitSequence checkpointSequence,
        CommitSequence floor,
        IReadOnlyList<CommittedVersionSnapshot> versions)
        => new(
            databaseId,
            historyId,
            checkpointSequence,
            floor,
            versions.Select(version => new HistoryCheckpointVersion(
                version.TransactionId,
                version.CommitSequence,
                version.Key,
                version.IsDelete,
                version.Value.ToArray())).ToArray());

    private List<object> AcquireMaintenanceCommitGates(BranchRuntime[] runtimes)
    {
        var acquired = new List<object>(runtimes.Length + 1);
        try
        {
            Monitor.Enter(_commitGate);
            acquired.Add(_commitGate);
            foreach (var runtime in runtimes)
            {
                Monitor.Enter(runtime.CommitGate);
                acquired.Add(runtime.CommitGate);
            }
            return acquired;
        }
        catch
        {
            ReleaseMaintenanceCommitGates(acquired);
            throw;
        }
    }

    private static void ReleaseMaintenanceCommitGates(List<object> acquired)
    {
        for (var index = acquired.Count - 1; index >= 0; index--)
        {
            Monitor.Exit(acquired[index]);
        }
    }

    private int ReclaimDeletedBranchDirectories()
    {
        var reclaimed = 0;
        foreach (var deleted in _branchStore.ListDeleted())
        {
            var directory = BranchStorageLayout.GetDirectory(_databaseDirectory, deleted.BranchId);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
            reclaimed++;
        }
        return reclaimed;
    }

    private IEnumerable<CompactionCandidate> BuildCompactionCandidates(
        IReadOnlyList<BranchRuntime> runtimes,
        CompactionOptions options)
    {
        var mainState = _store.SnapshotCurrentState();
        var mainTargetBytes = _store.EstimateRewriteDataLength(mainState);
        var mainReclaimable = Math.Max(0, _store.DataLength - mainTargetBytes);
        if (mainReclaimable >= options.MinimumReclaimableBytes)
        {
            yield return new CompactionCandidate(
                Runtime: null,
                DesiredState: mainState,
                ReclaimableBytes: mainReclaimable,
                EstimatedRewriteBytes: mainTargetBytes);
        }

        foreach (var candidate in runtimes
                     .Select(runtime =>
                     {
                         var desired = EncodeRetainedBranchPhysicalState(runtime);
                         var targetBytes = runtime.Store.EstimateRewriteDataLength(desired);
                         return new CompactionCandidate(
                             runtime,
                             desired,
                             Math.Max(0, runtime.Store.DataLength - targetBytes),
                             targetBytes);
                     })
                     .Where(candidate => candidate.ReclaimableBytes >= options.MinimumReclaimableBytes)
                     .OrderByDescending(candidate => candidate.ReclaimableBytes)
                     .ThenBy(candidate => candidate.Runtime!.Definition.BranchId.Value))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<CompactionCandidate> SelectCompactionCandidates(
        IEnumerable<CompactionCandidate> candidates,
        CompactionOptions options)
    {
        var count = 0;
        long rewriteBudgetUsed = 0;
        foreach (var candidate in candidates
                     .OrderByDescending(item => item.ReclaimableBytes)
                     .ThenBy(item => item.Runtime?.Definition.BranchId.Value ?? Guid.Empty))
        {
            if (count >= options.MaxHistoriesPerPass)
            {
                yield break;
            }

            if (candidate.EstimatedRewriteBytes > options.MaxBytesRewrittenPerPass - rewriteBudgetUsed)
            {
                continue;
            }

            rewriteBudgetUsed = checked(rewriteBudgetUsed + candidate.EstimatedRewriteBytes);
            count++;
            yield return candidate;
        }
    }

    private static IReadOnlyList<StorageMutation> EncodeRetainedBranchPhysicalState(BranchRuntime runtime)
        => BranchRuntime.EncodePhysicalState(runtime.Definition, runtime.Versions.SnapshotHistory());

    private sealed record CompactionCandidate(
        BranchRuntime? Runtime,
        IReadOnlyList<StorageMutation> DesiredState,
        long ReclaimableBytes,
        long EstimatedRewriteBytes);
}
