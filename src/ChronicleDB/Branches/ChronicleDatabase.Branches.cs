using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.History.Branches;
using ChronicleDB.History.Roots;
using ChronicleDB.History.Snapshots;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.HistoryRoots;
using ChronicleDB.Storage.Snapshots;
using ChronicleDB.Transactions;
using ChronicleDB.Transactions.Faults;
using ChronicleDB.Transactions.Mvcc;
using ChronicleDB.Transactions.State;
using ChronicleDB.Transactions.Writes;
using ChronicleDB.Wal.Records;

namespace ChronicleDB;

public sealed partial class ChronicleDatabase
{
    public ChronicleBranch CreateBranch(string name)
    {
        EnterOperation();
        try
        {
            return CreateBranchCore(
                _mainHistoryId,
                GetCurrentCommitSequence(),
                parentDepth: 0,
                name);
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleBranch CreateBranch(string name, ulong parentSequence)
    {
        EnterOperation();
        try
        {
            var boundary = new CommitSequence(parentSequence);
            ValidateHistoricalBoundary(boundary);
            return CreateBranchCore(_mainHistoryId, boundary, parentDepth: 0, name);
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleBranch CreateBranchFromSnapshot(Guid snapshotId, string name)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var id = new SnapshotId(snapshotId);
                if (!id.IsValid || !_snapshots.TryGet(id, out var snapshot) || snapshot is null)
                {
                    throw new SnapshotNotFoundException(snapshotId.ToString());
                }

                // Serialize source-root lookup with snapshot deletion. A named snapshot
                // may intentionally live below the generic time-travel floor after v0.9
                // GC, so temporarily register its already-retained boundary as an active
                // observer while the independent BranchBase root is durably established.
                // Once that root exists, the source snapshot may be deleted independently.
                var boundaryToken = _activeHistoryBoundaries.Register(_mainHistoryId, snapshot.Sequence);
                try
                {
                    return CreateBranchCore(_mainHistoryId, snapshot.Sequence, parentDepth: 0, name);
                }
                finally
                {
                    _activeHistoryBoundaries.Release(boundaryToken);
                }
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleBranch CreateBranchFromPinnedMainBoundary(CommitSequence boundary, string name)
    {
        EnterOperation();
        try
        {
            var current = GetCurrentCommitSequence();
            if (boundary > current || !_activeHistoryBoundaries.Contains(_mainHistoryId, boundary))
            {
                throw new HistoricalStateUnavailableException(
                    boundary.Value,
                    GetHistoryRetentionFloor().Value,
                    current.Value);
            }
            return CreateBranchCore(_mainHistoryId, boundary, parentDepth: 0, name);
        }
        finally
        {
            ExitOperation();
        }
    }

    public IReadOnlyList<ChronicleBranchInfo> ListBranches()
    {
        EnterOperation();
        try
        {
            return _branches.List().Select(ToBranchInfo).ToArray();
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleBranch OpenBranch(Guid branchId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var id = new BranchId(branchId);
                if (!id.IsValid || !_branches.TryGet(id, out var definition) || definition is null)
                {
                    throw new BranchNotFoundException(branchId.ToString());
                }

                var runtime = GetBranchRuntime(id);
                runtime.AcquireBranchHandle();
                return new ChronicleBranch(this, ToBranchInfo(definition));
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public ChronicleBranch OpenBranch(string name)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                if (!_branches.TryGet(name, out var definition) || definition is null)
                {
                    throw new BranchNotFoundException($"named '{name}'");
                }

                var runtime = GetBranchRuntime(definition.BranchId);
                runtime.AcquireBranchHandle();
                return new ChronicleBranch(this, ToBranchInfo(definition));
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleBranch CreateBranchFromBranch(BranchId parentBranchId, ulong sequence, string name)
    {
        EnterOperation();
        try
        {
            var parent = _branches.GetRequired(parentBranchId);
            var boundary = new CommitSequence(sequence);
            ValidateBranchHistoricalBoundary(parent, boundary);
            return CreateBranchCore(parent.HistoryId, boundary, parent.Depth, name);
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleBranch CreateBranchFromPinnedBranchBoundary(
        BranchId parentBranchId,
        CommitSequence boundary,
        string name)
    {
        EnterOperation();
        try
        {
            var parent = _branches.GetRequired(parentBranchId);
            if (boundary > parent.LocalCurrentSequence
                || !_activeHistoryBoundaries.Contains(parent.HistoryId, boundary))
            {
                var floor = GetBranchRuntime(parent.BranchId).HistoryFloor;
                throw new BranchHistoricalStateUnavailableException(
                    parent.BranchId.Value,
                    boundary.Value,
                    floor.Value,
                    parent.LocalCurrentSequence.Value);
            }
            return CreateBranchCore(parent.HistoryId, boundary, parent.Depth, name);
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleBranchInfo GetBranchInfo(BranchId branchId)
    {
        EnterOperation();
        try
        {
            return ToBranchInfo(_branches.GetRequired(branchId));
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleTransaction BeginBranchTransaction(BranchId branchId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var definition = _branches.GetRequired(branchId);
                var runtime = GetBranchRuntime(branchId);
                var transaction = new Transaction(
                    startSequence: definition.LocalCurrentSequence,
                    historyId: definition.HistoryId);
                transaction.Begin();
                var boundaryToken = _activeHistoryBoundaries.Register(definition.HistoryId, transaction.StartSequence);
                runtime.TransactionStarted();
                _counters.TransactionStarted();
                return new ChronicleTransaction(new BranchTransactionHost(this, branchId, boundaryToken), transaction);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool ReadBranchCurrent(BranchId branchId, ReadOnlySpan<byte> key, out byte[] value)
    {
        EnterOperation();
        try
        {
            var definition = _branches.GetRequired(branchId);
            var binaryKey = new BinaryKey(key);
            var runtime = GetBranchRuntime(branchId);
            var resolution = runtime.Versions.ResolveLatest(binaryKey);
            HistoryReadObservation observation;
            bool found;
            switch (resolution.Kind)
            {
                case CommittedVersionResolutionKind.Value:
                    value = resolution.Value;
                    found = true;
                    observation = new HistoryReadObservation(
                        ResearchReadResolutionKind.LocalValue,
                        AncestorProbes: 0,
                        ResolvedDepth: 0,
                        definition.HistoryId,
                        ResolvedBoundary: null);
                    break;
                case CommittedVersionResolutionKind.Tombstone:
                    value = [];
                    found = false;
                    observation = new HistoryReadObservation(
                        ResearchReadResolutionKind.LocalTombstone,
                        AncestorProbes: 0,
                        ResolvedDepth: 0,
                        definition.HistoryId,
                        ResolvedBoundary: null);
                    break;
                case CommittedVersionResolutionKind.NoVisibleVersion:
                    found = ResolveHistoryReadCore(
                        definition.ParentHistoryId,
                        definition.ParentBaseSequence,
                        binaryKey,
                        initialDepth: 1,
                        out value,
                        out observation);
                    break;
                default:
                    throw new InvalidOperationException("Unknown committed-version resolution result.");
            }

            PublishHistoryReadObservation(definition, binaryKey, observation);
            return found;
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool ReadBranchAt(
        BranchId branchId,
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
    {
        EnterOperation();
        try
        {
            var definition = _branches.GetRequired(branchId);
            ValidateBranchHistoricalBoundary(definition, visibilityBoundary);
            var binaryKey = new BinaryKey(key);
            var found = ResolveHistoryReadCore(
                definition.HistoryId,
                visibilityBoundary,
                binaryKey,
                initialDepth: 0,
                out value,
                out var observation);
            PublishHistoryReadObservation(definition, binaryKey, observation);
            return found;
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool ReadBranchPinnedAt(
        BranchId branchId,
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
    {
        EnterOperation();
        try
        {
            var definition = _branches.GetRequired(branchId);
            if (visibilityBoundary > definition.LocalCurrentSequence)
            {
                var floor = GetBranchRuntime(definition.BranchId).HistoryFloor;
                throw new BranchHistoricalStateUnavailableException(
                    definition.BranchId.Value,
                    visibilityBoundary.Value,
                    floor.Value,
                    definition.LocalCurrentSequence.Value);
            }

            var binaryKey = new BinaryKey(key);
            var found = ResolveHistoryReadCore(
                definition.HistoryId,
                visibilityBoundary,
                binaryKey,
                initialDepth: 0,
                out value,
                out var observation);
            PublishHistoryReadObservation(definition, binaryKey, observation);
            return found;
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool ReadBranchHistorical(
        BranchId branchId,
        ReadOnlySpan<byte> key,
        CommitSequence visibilityBoundary,
        out byte[] value)
        => ReadBranchPinnedAt(branchId, key, visibilityBoundary, out value);

    internal ChronicleBranchHistoricalView OpenBranchHistoricalView(BranchId branchId, ulong sequence)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var definition = _branches.GetRequired(branchId);
                var boundary = new CommitSequence(sequence);
                ValidateBranchHistoricalBoundary(definition, boundary);
                var runtime = GetBranchRuntime(branchId);
                var boundaryToken = _activeHistoryBoundaries.Register(definition.HistoryId, boundary);
                runtime.HistoricalHandleOpened();
                return new ChronicleBranchHistoricalView(this, branchId, sequence, boundaryToken);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleBranchSnapshot CreateBranchSnapshot(BranchId branchId, string name)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var definition = _branches.GetRequired(branchId);
                var runtime = GetBranchRuntime(branchId);
                SnapshotDefinition snapshot;
                try
                {
                    snapshot = runtime.Snapshots.PrepareCreate(name, definition.LocalCurrentSequence);
                }
                catch (InvalidOperationException)
                {
                    throw new SnapshotNameConflictException(name);
                }

                var metadataAppended = false;
                try
                {
                    var root = ToBranchSnapshotRoot(snapshot, definition);
                    runtime.SnapshotStore.AppendCreate(
                        snapshot.SnapshotId,
                        snapshot.Sequence,
                        snapshot.CreatedUnixMilliseconds,
                        snapshot.Name);
                    metadataAppended = true;
                    _historyRootStore.AppendCreate(ToHistoryRootStoreRecord(root));
                    _historyRoots.RegisterActive(root);
                    runtime.Snapshots.RegisterPersisted(snapshot, definition.LocalCurrentSequence);
                    var boundaryToken = _activeHistoryBoundaries.Register(definition.HistoryId, snapshot.Sequence);
                    runtime.HistoricalHandleOpened();
                    return new ChronicleBranchSnapshot(this, ToBranchSnapshotInfo(snapshot, definition), boundaryToken);
                }
                catch
                {
                    if (runtime.SnapshotStore.IsFaulted || _historyRootStore.IsFaulted || metadataAppended)
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

    internal IReadOnlyList<ChronicleBranchSnapshotInfo> ListBranchSnapshots(BranchId branchId)
    {
        EnterOperation();
        try
        {
            var definition = _branches.GetRequired(branchId);
            var runtime = GetBranchRuntime(branchId);
            return runtime.Snapshots.List().Select(snapshot => ToBranchSnapshotInfo(snapshot, definition)).ToArray();
        }
        finally
        {
            ExitOperation();
        }
    }

    internal ChronicleBranchSnapshot OpenBranchSnapshot(BranchId branchId, Guid snapshotId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var definition = _branches.GetRequired(branchId);
                var runtime = GetBranchRuntime(branchId);
                var id = new SnapshotId(snapshotId);
                if (!id.IsValid || !runtime.Snapshots.TryGet(id, out var snapshot) || snapshot is null)
                {
                    throw new SnapshotNotFoundException(snapshotId.ToString());
                }

                var boundaryToken = _activeHistoryBoundaries.Register(definition.HistoryId, snapshot.Sequence);
                runtime.HistoricalHandleOpened();
                return new ChronicleBranchSnapshot(this, ToBranchSnapshotInfo(snapshot, definition), boundaryToken);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal void DeleteBranchSnapshot(BranchId branchId, Guid snapshotId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var runtime = GetBranchRuntime(branchId);
                var id = new SnapshotId(snapshotId);
                if (!id.IsValid || !runtime.Snapshots.TryGet(id, out var snapshot) || snapshot is null)
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
                    runtime.SnapshotStore.AppendDelete(id);
                    metadataAppended = true;
                    _historyRootStore.AppendDelete(rootId);
                    runtime.Snapshots.RemoveRequired(id);
                    _historyRoots.CompleteDelete(rootId);
                }
                catch
                {
                    if (deletionStarted && !metadataAppended)
                    {
                        _historyRoots.CancelDelete(rootId);
                    }
                    if (runtime.SnapshotStore.IsFaulted || _historyRootStore.IsFaulted || metadataAppended)
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

    internal void BranchHandleClosed(BranchId branchId)
    {
        if (_branchRuntimes.TryGetValue(branchId, out var runtime))
        {
            runtime.ReleaseBranchHandle();
        }
    }

    internal void BranchTransactionHandleCompleted(BranchId branchId, long boundaryToken)
    {
        _activeHistoryBoundaries.Release(boundaryToken);
        if (_branchRuntimes.TryGetValue(branchId, out var runtime))
        {
            runtime.TransactionCompleted();
        }
        _counters.TransactionFinished();
    }

    internal void BranchHistoricalHandleClosed(BranchId branchId, long boundaryToken)
    {
        _activeHistoryBoundaries.Release(boundaryToken);
        if (_branchRuntimes.TryGetValue(branchId, out var runtime))
        {
            runtime.HistoricalHandleClosed();
        }
    }

    public void DeleteBranch(Guid branchId)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var id = new BranchId(branchId);
                if (!id.IsValid || !_branches.TryGet(id, out var definition) || definition is null)
                {
                    throw new BranchNotFoundException(branchId.ToString());
                }
                DeleteBranchCore(definition);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public void DeleteBranch(string name)
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                if (!_branches.TryGet(name, out var definition) || definition is null)
                {
                    throw new BranchNotFoundException($"named '{name}'");
                }
                DeleteBranchCore(definition);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private void DeleteBranchCore(BranchDefinition definition)
    {
        var id = definition.BranchId;
        var runtime = GetBranchRuntime(id);
        EnsureBranchDeletionAllowed(definition, runtime);
        var researchOperationId = Guid.NewGuid();
        var researchResources = new[]
        {
            $"branch-{id.Value:N}-data",
            $"branch-{id.Value:N}-wal",
            "branch-catalog",
            "history-roots",
        };
        var operationStartedEventId = PublishResearchPersistenceEvent(
            definition.HistoryId,
            definition.ParentHistoryId,
            researchOperationId,
            researchResources,
            ResearchEventKind.OperationStarted,
            ResearchDurabilityPhase.Prepared,
            definition.LocalCurrentSequence.Value,
            []);
        var intentPublished = false;
        try
        {
            _branchStore.AppendDeleteIntent(id);
            intentPublished = true;
            var barrierEventId = PublishResearchPersistenceEvent(
                definition.HistoryId,
                definition.ParentHistoryId,
                researchOperationId,
                researchResources,
                ResearchEventKind.DurabilityBarrier,
                ResearchDurabilityPhase.StableStorageBarrier,
                definition.LocalCurrentSequence.Value,
                operationStartedEventId > 0 ? [operationStartedEventId] : []);
            _historyRootStore.AppendDelete(definition.BaseRootId);
            _historyRoots.BeginDelete(definition.BaseRootId);
            _historyRoots.CompleteDelete(definition.BaseRootId);
            _branchStore.AppendDeleteComplete(id);
            var authorityEventId = PublishResearchPersistenceEvent(
                definition.HistoryId,
                definition.ParentHistoryId,
                researchOperationId,
                researchResources,
                ResearchEventKind.AuthorityPublished,
                ResearchDurabilityPhase.AuthorityPublished,
                definition.LocalCurrentSequence.Value,
                barrierEventId > 0 ? [barrierEventId] : []);

            _branches.RemoveRequired(id);
            _branchRuntimes.TryRemove(id, out _);
            _historyRoots.UnregisterHistory(definition.HistoryId);
            runtime.Dispose();
            PublishResearchPersistenceEvent(
                definition.HistoryId,
                definition.ParentHistoryId,
                researchOperationId,
                researchResources,
                ResearchEventKind.OperationCompleted,
                ResearchDurabilityPhase.Cleanup,
                definition.LocalCurrentSequence.Value,
                authorityEventId > 0 ? [authorityEventId] : []);
        }
        catch
        {
            if (intentPublished || _branchStore.IsFaulted || _historyRootStore.IsFaulted)
            {
                MarkFaulted();
            }
            throw;
        }
    }

    private void EnsureBranchDeletionAllowed(BranchDefinition definition, BranchRuntime runtime)
    {
        if (runtime.OpenBranchHandles != 0)
        {
            throw new BranchInUseException(definition.BranchId.Value, "one or more branch handles are open");
        }
        if (runtime.ActiveTransactions != 0)
        {
            throw new BranchInUseException(definition.BranchId.Value, "transactions are still active");
        }
        if (runtime.OpenHistoricalHandles != 0)
        {
            throw new BranchInUseException(definition.BranchId.Value, "historical or snapshot handles are still open");
        }
        if (runtime.Snapshots.Count != 0)
        {
            throw new BranchInUseException(definition.BranchId.Value, "persistent snapshots still depend on the branch history");
        }
        if (_branches.List().Any(branch => branch.ParentHistoryId == definition.HistoryId))
        {
            throw new BranchInUseException(definition.BranchId.Value, "one or more child branches depend on it");
        }
    }

    internal void CommitBranch(BranchId branchId, Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnterOperation();
        _counters.CommitAttempted();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var runtime = GetBranchRuntime(branchId);
            lock (runtime.CommitGate)
            {
                ThrowIfUsable();
                var definition = _branches.GetRequired(branchId);
                if (transaction.HistoryId != definition.HistoryId)
                {
                    throw new InvalidOperationException(
                        "A branch transaction may only be committed through the branch history that created it.");
                }

                var writes = transaction.PrepareAndGetWriteSet();
                CommitSequence commitSequence;
                List<StorageMutation> logicalMutations;
                List<StorageMutation> physicalMutations;
                List<(WalRecordType Type, byte[] Payload)> walPayloads;
                byte[] commitPayload;
                long startingDataLength;
                try
                {
                    ValidateBranchWriteConflicts(runtime, transaction, writes);
                    try
                    {
                        commitSequence = definition.LocalCurrentSequence.Next();
                    }
                    catch (OverflowException exception)
                    {
                        throw new InvalidOperationException("The branch commit-sequence space is exhausted.", exception);
                    }

                    logicalMutations = writes.Select(write => new StorageMutation(
                        write.Key,
                        write.IsDelete,
                        write.Value.Span)).ToList();
                    _store.ValidateBatch(logicalMutations);
                    runtime.Versions.ValidatePublicationCapacity(writes);
                    physicalMutations = EncodeBranchVersionMutations(definition, transaction, commitSequence, writes);
                    runtime.Store.ValidateBatch(physicalMutations);
                    startingDataLength = runtime.Store.DataLength;
                    _branchStore.ValidateAdvance(
                        branchId,
                        commitSequence,
                        transaction.TransactionId,
                        writes.Count,
                        startingDataLength);

                    walPayloads = new List<(WalRecordType Type, byte[] Payload)>(writes.Count);
                    foreach (var write in writes)
                    {
                        var inner = write.IsDelete
                            ? WalMutationCodec.EncodeDelete(write.Key)
                            : WalMutationCodec.EncodePut(write.Key, write.Value.Span);
                        var wrapped = BranchRuntime.WrapPayload(definition, inner);
                        ValidateWalPayload(wrapped);
                        walPayloads.Add((write.IsDelete ? WalRecordType.Delete : WalRecordType.Put, wrapped));
                    }
                    commitPayload = BranchRuntime.WrapPayload(
                        definition,
                        WalCommitCodec.Encode(commitSequence, startingDataLength));
                    ValidateWalPayload(commitPayload);
                    ValidateWalCapacity(runtime.Wal, walPayloads.Count + 2);
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
                    var operationStartedEventId = PublishResearchTransactionEvent(
                        definition.HistoryId,
                        definition.ParentHistoryId,
                        [$"branch-{definition.BranchId.Value:N}-data", $"branch-{definition.BranchId.Value:N}-wal"],
                        transaction,
                        ResearchEventKind.OperationStarted,
                        ResearchDurabilityPhase.Prepared,
                        commitSequence,
                        []);
                    _faultInjector?.Hit(TransactionFaultPoint.BeforeWalAppend);
                    walTouched = true;
                    runtime.Wal.Append(
                        WalRecordType.Begin,
                        transaction.TransactionId,
                        BranchRuntime.WrapPayload(definition, []));
                    foreach (var (type, payload) in walPayloads)
                    {
                        runtime.Wal.Append(type, transaction.TransactionId, payload);
                    }

                    transaction.MarkCommitting();
                    runtime.Wal.Append(WalRecordType.Commit, transaction.TransactionId, commitPayload);
                    _faultInjector?.Hit(TransactionFaultPoint.AfterWalAppend);
                    _faultInjector?.Hit(TransactionFaultPoint.BeforeWalFlush);
                    runtime.Wal.Flush();
                    transaction.MarkDurableCommitted(commitSequence);
                    var barrierEventId = PublishResearchTransactionEvent(
                        definition.HistoryId,
                        definition.ParentHistoryId,
                        [$"branch-{definition.BranchId.Value:N}-data", $"branch-{definition.BranchId.Value:N}-wal"],
                        transaction,
                        ResearchEventKind.DurabilityBarrier,
                        ResearchDurabilityPhase.StableStorageBarrier,
                        commitSequence,
                        operationStartedEventId > 0 ? [operationStartedEventId] : []);
                    _faultInjector?.Hit(TransactionFaultPoint.AfterWalFlush);

                    _faultInjector?.Hit(TransactionFaultPoint.BeforePhysicalPublication);
                    runtime.Store.ApplyBatch(physicalMutations);
                    var dataLengthAfterCommit = runtime.Store.DataLength;
                    _branchStore.AppendAdvance(
                        branchId,
                        commitSequence,
                        transaction.TransactionId,
                        writes.Count,
                        dataLengthAfterCommit);
                    _faultInjector?.Hit(TransactionFaultPoint.AfterPhysicalPublication);

                    runtime.Versions.PublishCommitted(transaction.TransactionId, commitSequence, writes);
                    var updated = _branches.PublishCommit(
                        branchId,
                        definition.LocalCurrentSequence,
                        commitSequence);
                    runtime.PublishDefinition(updated);
                    transaction.MarkCommitted();
                    var authorityEventId = PublishResearchTransactionEvent(
                        definition.HistoryId,
                        definition.ParentHistoryId,
                        [$"branch-{definition.BranchId.Value:N}-data", $"branch-{definition.BranchId.Value:N}-wal", "branch-catalog"],
                        transaction,
                        ResearchEventKind.AuthorityPublished,
                        ResearchDurabilityPhase.AuthorityPublished,
                        commitSequence,
                        barrierEventId > 0 ? [barrierEventId] : []);
                    _counters.CommitSucceeded(Stopwatch.GetTimestamp() - started);
                    _faultInjector?.Hit(TransactionFaultPoint.BeforeAcknowledgement);
                    PublishResearchTransactionEvent(
                        definition.HistoryId,
                        definition.ParentHistoryId,
                        [$"branch-{definition.BranchId.Value:N}-data", $"branch-{definition.BranchId.Value:N}-wal", "branch-catalog"],
                        transaction,
                        ResearchEventKind.OperationCompleted,
                        ResearchDurabilityPhase.AuthorityPublished,
                        commitSequence,
                        authorityEventId > 0 ? [authorityEventId] : []);
                }
                catch
                {
                    if (walTouched)
                    {
                        MarkFaulted();
                        if (transaction.State is TransactionState.Preparing or TransactionState.Committing)
                        {
                            runtime.Wal.MarkFaultedAfterUncertainWrite();
                            transaction.MarkIndeterminate();
                        }
                        else if (transaction.State == TransactionState.DurableCommitted)
                        {
                            // The durable decision is already final. In-memory/physical
                            // publication must be reconstructed from branch WAL on reopen.
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
        }
        finally
        {
            ExitOperation();
        }
    }

    private ChronicleBranch CreateBranchCore(
        HistoryId parentHistoryId,
        CommitSequence parentBaseSequence,
        int parentDepth,
        string name)
    {
        lock (_historyGate)
        {
            BranchCatalog.ValidateName(name);
            if (parentDepth >= BranchCatalog.MaximumDepth)
            {
                throw new InvalidOperationException(
                    $"ChronicleDB v1.0 supports at most {BranchCatalog.MaximumDepth} nested branch levels.");
            }

            ValidateHistoryBoundary(parentHistoryId, parentBaseSequence);
            try
            {
                _branches.EnsureNameAvailable(name);
                _branchStore.EnsureNameAvailable(name);
            }
            catch (InvalidOperationException)
            {
                throw new BranchNameConflictException(name);
            }
            catch (StorageException exception) when (exception.Message.Contains("name", StringComparison.OrdinalIgnoreCase))
            {
                throw new BranchNameConflictException(name);
            }

            var branchId = BranchId.New();
            var historyId = HistoryId.New();
            var rootId = HistoryRootId.New();
            var researchOperationId = Guid.NewGuid();
            var researchResources = new[]
            {
                $"branch-{branchId.Value:N}-data",
                "branch-catalog",
                "history-roots",
            };
            var operationStartedEventId = PublishResearchPersistenceEvent(
                historyId,
                parentHistoryId,
                researchOperationId,
                researchResources,
                ResearchEventKind.OperationStarted,
                ResearchDurabilityPhase.Prepared,
                (ulong)parentBaseSequence.Value,
                []);
            var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var depth = checked(parentDepth + 1);
            // Complete deterministic branch-local format validation before the first
            // persistent creation intent is written. A configuration that cannot encode
            // branch version envelopes must fail without polluting lifecycle metadata.
            var localOptions = BranchStorageLayout.CreateLocalStorageOptions(_storageOptions);
            var localDirectory = BranchStorageLayout.GetDirectory(_databaseDirectory, branchId);
            if (Directory.Exists(localDirectory))
            {
                throw new StorageCorruptionException(
                    $"New branch storage path '{localDirectory}' already exists.");
            }

            var intentPersisted = false;
            var rootPersisted = false;
            var activated = false;
            try
            {
                _branchStore.AppendCreateIntent(
                    branchId,
                    historyId,
                    parentHistoryId,
                    rootId,
                    parentBaseSequence,
                    created,
                    depth,
                    name);
                intentPersisted = true;
                var barrierEventId = PublishResearchPersistenceEvent(
                    historyId,
                    parentHistoryId,
                    researchOperationId,
                    researchResources,
                    ResearchEventKind.DurabilityBarrier,
                    ResearchDurabilityPhase.StableStorageBarrier,
                    (ulong)parentBaseSequence.Value,
                    operationStartedEventId > 0 ? [operationStartedEventId] : []);

                Directory.CreateDirectory(Path.GetDirectoryName(localDirectory)!);
                Guid localStorageId;
                using (var localStore = PersistentKeyValueStore.Open(
                           localDirectory,
                           localOptions))
                {
                    localStorageId = localStore.DatabaseId;
                }

                var root = new HistoryRoot(
                    rootId,
                    HistoryRootKind.BranchBase,
                    _databaseId,
                    historyId,
                    parentHistoryId,
                    parentBaseSequence,
                    created,
                    HistoryRootState.Active);
                _historyRootStore.AppendCreate(ToHistoryRootStoreRecord(root));
                rootPersisted = true;

                var activeRecord = _branchStore.AppendActivate(branchId, localStorageId);
                activated = true;
                var authorityEventId = PublishResearchPersistenceEvent(
                    historyId,
                    parentHistoryId,
                    researchOperationId,
                    researchResources,
                    ResearchEventKind.AuthorityPublished,
                    ResearchDurabilityPhase.AuthorityPublished,
                    (ulong)parentBaseSequence.Value,
                    barrierEventId > 0 ? [barrierEventId] : []);
                var definition = ToBranchDefinition(activeRecord, _databaseId);
                var runtime = BranchRuntime.Open(
                    _databaseDirectory,
                    definition,
                    activeRecord,
                    _branchStore.ListCommits(branchId),
                    _branchStore,
                    _storageOptions);

                _branches.RegisterActive(definition);
                _historyRoots.RegisterHistory(historyId, CommitSequence.Initial);
                _historyRoots.RegisterActive(root);
                if (!_branchRuntimes.TryAdd(branchId, runtime))
                {
                    runtime.Dispose();
                    throw new InvalidOperationException("Branch runtime identity was published twice.");
                }

                runtime.AcquireBranchHandle();
                PublishResearchPersistenceEvent(
                    historyId,
                    parentHistoryId,
                    researchOperationId,
                    researchResources,
                    ResearchEventKind.OperationCompleted,
                    ResearchDurabilityPhase.AuthorityPublished,
                    (ulong)parentBaseSequence.Value,
                    authorityEventId > 0 ? [authorityEventId] : []);
                return new ChronicleBranch(this, ToBranchInfo(runtime.Definition));
            }
            catch
            {
                if (!activated && intentPersisted && !_branchStore.IsFaulted && !_historyRootStore.IsFaulted)
                {
                    try
                    {
                        if (rootPersisted
                            && _historyRootStore.TryGet(rootId, out var existing)
                            && existing is not null
                            && existing.RootState != (byte)HistoryRootState.Deleted)
                        {
                            _historyRootStore.AppendDelete(rootId);
                        }
                        _branchStore.AppendAbandonCreate(branchId);
                    }
                    catch
                    {
                        MarkFaulted();
                    }
                }

                if (activated || _branchStore.IsFaulted || _historyRootStore.IsFaulted)
                {
                    MarkFaulted();
                }
                throw;
            }
        }
    }

    private bool ResolveHistoryReadCore(
        HistoryId historyId,
        CommitSequence boundary,
        BinaryKey key,
        int initialDepth,
        out byte[] value,
        out HistoryReadObservation observation)
    {
        var currentHistoryId = historyId;
        var currentBoundary = boundary;
        var depth = initialDepth;

        while (true)
        {
            CommittedVersionResolution resolution;
            if (currentHistoryId == _mainHistoryId)
            {
                resolution = _versions.Resolve(key, currentBoundary);
            }
            else
            {
                if (!_branches.TryGetByHistory(currentHistoryId, out var definition) || definition is null)
                {
                    throw new StorageCorruptionException(
                        $"History {currentHistoryId.Value} has no active branch owner.");
                }

                var runtime = GetBranchRuntime(definition.BranchId);
                resolution = runtime.Versions.Resolve(key, currentBoundary);
                if (resolution.Kind == CommittedVersionResolutionKind.NoVisibleVersion)
                {
                    currentHistoryId = definition.ParentHistoryId;
                    currentBoundary = definition.ParentBaseSequence;
                    depth = checked(depth + 1);
                    continue;
                }
            }

            var readKind = resolution.Kind switch
            {
                CommittedVersionResolutionKind.Value when depth == 0 => ResearchReadResolutionKind.LocalValue,
                CommittedVersionResolutionKind.Tombstone when depth == 0 => ResearchReadResolutionKind.LocalTombstone,
                CommittedVersionResolutionKind.Value => ResearchReadResolutionKind.InheritedValue,
                CommittedVersionResolutionKind.Tombstone => ResearchReadResolutionKind.InheritedTombstone,
                CommittedVersionResolutionKind.NoVisibleVersion => ResearchReadResolutionKind.Missing,
                _ => throw new InvalidOperationException("Unknown committed-version resolution result."),
            };

            value = resolution.Kind == CommittedVersionResolutionKind.Value
                ? resolution.Value
                : [];
            observation = new HistoryReadObservation(
                readKind,
                AncestorProbes: depth,
                ResolvedDepth: resolution.Kind == CommittedVersionResolutionKind.NoVisibleVersion ? null : depth,
                resolution.Kind == CommittedVersionResolutionKind.NoVisibleVersion ? null : currentHistoryId,
                resolution.Kind == CommittedVersionResolutionKind.NoVisibleVersion ? null : currentBoundary);
            return resolution.Kind == CommittedVersionResolutionKind.Value;
        }
    }

    private void PublishHistoryReadObservation(
        BranchDefinition requestedHistory,
        BinaryKey key,
        HistoryReadObservation observation)
    {
        if (_researchEvents.Mode == ResearchTelemetryMode.Disabled)
        {
            return;
        }

        var logicalKeyId = _researchEvents.Mode == ResearchTelemetryMode.Trace
            ? Convert.ToHexString(SHA256.HashData(key.AsSpan())).ToLowerInvariant()
            : null;
        var readObservation = new ResearchReadObservation(
            observation.Resolution,
            observation.AncestorProbes,
            observation.ResolvedDepth,
            observation.ResolvedHistoryId);

        _researchEvents.TryPublish(
            logicalEventId => new ResearchEvent(
                logicalEventId,
                logicalEventId,
                ResearchEventKind.HistoryReadObserved,
                requestedHistory.HistoryId,
                requestedHistory.ParentHistoryId,
                Guid.NewGuid(),
                transactionId: null,
                [ResearchReadTelemetry.Resource],
                ResearchDurabilityPhase.None,
                authorityGeneration: 0,
                dependencyEventIds: [],
                logicalKeyId,
                versionId: null,
                offset: null,
                bytes: null,
                readObservation),
            out _);
    }

    private readonly record struct HistoryReadObservation(
        ResearchReadResolutionKind Resolution,
        int AncestorProbes,
        int? ResolvedDepth,
        HistoryId? ResolvedHistoryId,
        CommitSequence? ResolvedBoundary);

    private void ValidateHistoryBoundary(HistoryId historyId, CommitSequence boundary)
    {
        if (historyId == _mainHistoryId)
        {
            if (_activeHistoryBoundaries.Contains(historyId, boundary))
            {
                if (boundary > GetCurrentCommitSequence())
                {
                    throw new HistoricalStateUnavailableException(
                        boundary.Value,
                        GetHistoryRetentionFloor().Value,
                        GetCurrentCommitSequence().Value);
                }
                return;
            }

            ValidateHistoricalBoundary(boundary);
            return;
        }

        if (!_branches.TryGetByHistory(historyId, out var definition) || definition is null)
        {
            throw new BranchNotFoundException($"for history {historyId.Value}");
        }
        if (_activeHistoryBoundaries.Contains(historyId, boundary))
        {
            if (boundary > definition.LocalCurrentSequence)
            {
                var floor = GetBranchRuntime(definition.BranchId).HistoryFloor;
                throw new BranchHistoricalStateUnavailableException(
                    definition.BranchId.Value,
                    boundary.Value,
                    floor.Value,
                    definition.LocalCurrentSequence.Value);
            }
            return;
        }
        ValidateBranchHistoricalBoundary(definition, boundary);
    }

    private void ValidateBranchHistoricalBoundary(BranchDefinition definition, CommitSequence boundary)
    {
        var floor = GetBranchRuntime(definition.BranchId).HistoryFloor;
        if (boundary < floor || boundary > definition.LocalCurrentSequence)
        {
            throw new BranchHistoricalStateUnavailableException(
                definition.BranchId.Value,
                boundary.Value,
                floor.Value,
                definition.LocalCurrentSequence.Value);
        }
    }

    private BranchRuntime GetBranchRuntime(BranchId branchId)
        => _branchRuntimes.TryGetValue(branchId, out var runtime)
            ? runtime
            : throw new BranchNotFoundException(branchId.Value.ToString());

    private static void ValidateBranchWriteConflicts(
        BranchRuntime runtime,
        Transaction transaction,
        IReadOnlyList<TransactionWrite> writes)
    {
        foreach (var write in writes)
        {
            if (runtime.Versions.TryGetLatestCommitSequence(write.Key, out var latest)
                && latest > transaction.StartSequence)
            {
                throw new TransactionConflictException(
                    transaction.TransactionId.Value,
                    transaction.StartSequence.Value,
                    latest.Value);
            }
        }
    }

    private static List<StorageMutation> EncodeBranchVersionMutations(
        BranchDefinition definition,
        Transaction transaction,
        CommitSequence commitSequence,
        IReadOnlyList<TransactionWrite> writes)
    {
        var result = new List<StorageMutation>(writes.Count);
        for (var i = 0; i < writes.Count; i++)
        {
            var write = writes[i];
            var record = new BranchVersionRecord(
                definition.BranchId,
                definition.HistoryId,
                transaction.TransactionId,
                commitSequence,
                i,
                writes.Count,
                write.Key.ToArray(),
                write.IsDelete,
                write.IsDelete ? [] : write.Value.ToArray());
            var physicalKey = CreateBranchPhysicalVersionKey(commitSequence, transaction.TransactionId, i);
            result.Add(new StorageMutation(physicalKey, isDelete: false, BranchVersionRecordCodec.Encode(record)));
        }
        return result;
    }

    internal static BinaryKey CreateBranchPhysicalVersionKey(
        CommitSequence sequence,
        TransactionId transactionId,
        int mutationIndex)
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), sequence.Value);
        transactionId.Value.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), checked((uint)mutationIndex));
        return new BinaryKey(bytes);
    }

    private static ChronicleBranchInfo ToBranchInfo(BranchDefinition definition)
        => new(
            definition.BranchId.Value,
            definition.OwnerDatabaseId,
            definition.Name,
            definition.HistoryId.Value,
            definition.ParentHistoryId.Value,
            definition.ParentBaseSequence.Value,
            definition.LocalCurrentSequence.Value,
            definition.Depth,
            DateTimeOffset.FromUnixTimeMilliseconds(definition.CreatedUnixMilliseconds));

    private static ChronicleBranchSnapshotInfo ToBranchSnapshotInfo(
        SnapshotDefinition snapshot,
        BranchDefinition branch)
        => new(
            snapshot.SnapshotId.Value,
            branch.BranchId.Value,
            branch.HistoryId.Value,
            snapshot.Name,
            snapshot.Sequence.Value,
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedUnixMilliseconds));

    private HistoryRoot ToBranchSnapshotRoot(SnapshotDefinition snapshot, BranchDefinition branch)
        => new(
            new HistoryRootId(snapshot.SnapshotId.Value),
            HistoryRootKind.PersistentSnapshot,
            _databaseId,
            branch.HistoryId,
            HistoryId.Empty,
            snapshot.Sequence,
            snapshot.CreatedUnixMilliseconds,
            HistoryRootState.Active);

    internal static BranchDefinition ToBranchDefinition(BranchStoreRecord record, Guid databaseId)
    {
        if (record.Type is not (BranchStoreRecordType.Activate
            or BranchStoreRecordType.AdvanceSequence
            or BranchStoreRecordType.PublishPhysicalBoundary
            or BranchStoreRecordType.RestoreActive))
        {
            throw new StorageFormatException("Only active branch metadata can become a branch definition.");
        }

        return new BranchDefinition(
            record.BranchId,
            record.Name,
            databaseId,
            record.HistoryId,
            record.ParentHistoryId,
            record.BaseRootId,
            record.ParentBaseSequence,
            record.LocalCommitSequence,
            record.LocalStorageId,
            record.CreatedUnixMilliseconds,
            record.Depth,
            BranchLifecycleState.Active);
    }

    internal static HistoryRootStoreRecord ToHistoryRootStoreRecord(HistoryRoot root)
        => new(
            HistoryRootStoreRecordType.Create,
            EventSequence: 0,
            root.RootId,
            (byte)root.Kind,
            (byte)HistoryRootState.Active,
            root.OwnerDatabaseId,
            root.HistoryId,
            root.ParentHistoryId,
            root.Boundary,
            root.CreatedUnixMilliseconds);

    internal static void ReconcileIncompleteBranchDeletions(
        PersistentBranchMetadataStore branchStore,
        PersistentHistoryRootStore rootStore)
    {
        var active = branchStore.ListActive();
        var retainingRoots = rootStore.ListRetaining();

        foreach (var deleting in branchStore.ListDeleting())
        {
            if (active.Any(branch => branch.ParentHistoryId == deleting.HistoryId))
            {
                throw new StorageCorruptionException(
                    $"Branch {deleting.BranchId.Value} has a durable delete intent while an active child still depends on it.");
            }

            if (retainingRoots.Any(root =>
                    root.RootKind == (byte)HistoryRootKind.PersistentSnapshot
                    && root.HistoryId == deleting.HistoryId))
            {
                throw new StorageCorruptionException(
                    $"Branch {deleting.BranchId.Value} has a durable delete intent while a persistent snapshot still depends on it.");
            }

            if (rootStore.TryGet(deleting.BaseRootId, out var baseRoot)
                && baseRoot is not null
                && baseRoot.RootState != (byte)HistoryRootState.Deleted)
            {
                rootStore.AppendDelete(deleting.BaseRootId);
            }

            branchStore.AppendDeleteComplete(deleting.BranchId);
        }
    }

    internal static void ReconcileIncompleteBranchCreations(
        string databaseDirectory,
        PersistentBranchMetadataStore branchStore,
        PersistentHistoryRootStore rootStore)
    {
        foreach (var creating in branchStore.ListCreating())
        {
            if (rootStore.TryGet(creating.BaseRootId, out var root)
                && root is not null
                && root.RootState != (byte)HistoryRootState.Deleted)
            {
                rootStore.AppendDelete(creating.BaseRootId);
            }

            // A creation intent that never reached Active owns no committed branch
            // history. Remove any partially initialized local directory before the
            // durable abandon marker so repeated recovery is idempotent and does not
            // leak unreachable branch-private files.
            var localDirectory = BranchStorageLayout.GetDirectory(databaseDirectory, creating.BranchId);
            if (Directory.Exists(localDirectory))
            {
                Directory.Delete(localDirectory, recursive: true);
            }

            branchStore.AppendAbandonCreate(creating.BranchId);
        }
    }

    internal static void ReconcileBranchBaseRoots(
        PersistentHistoryRootStore rootStore,
        IReadOnlyList<BranchDefinition> branches,
        Guid databaseId)
    {
        var expectedIds = new HashSet<HistoryRootId>();
        foreach (var branch in branches)
        {
            var root = new HistoryRoot(
                branch.BaseRootId,
                HistoryRootKind.BranchBase,
                databaseId,
                branch.HistoryId,
                branch.ParentHistoryId,
                branch.ParentBaseSequence,
                branch.CreatedUnixMilliseconds,
                HistoryRootState.Active);
            var expected = ToHistoryRootStoreRecord(root);
            expectedIds.Add(expected.RootId);
            if (!rootStore.TryGet(expected.RootId, out var existing) || existing is null)
            {
                rootStore.AppendCreate(expected);
                continue;
            }

            if (existing.RootState == (byte)HistoryRootState.Deleted
                || !RootMetadataMatches(existing, expected))
            {
                throw new StorageCorruptionException(
                    $"Branch-base root metadata for branch {branch.BranchId.Value} is inconsistent.");
            }
        }

        foreach (var root in rootStore.ListRetaining())
        {
            if (root.RootKind == (byte)HistoryRootKind.BranchBase && !expectedIds.Contains(root.RootId))
            {
                rootStore.AppendDelete(root.RootId);
            }
        }
    }

    internal static void ReconcileBranchSnapshotRoots(
        PersistentHistoryRootStore rootStore,
        BranchDefinition branch,
        IReadOnlyList<SnapshotStoreRecord> snapshots,
        Guid databaseId)
    {
        var expectedIds = new HashSet<HistoryRootId>();
        foreach (var snapshotRecord in snapshots)
        {
            var snapshot = new SnapshotDefinition(
                snapshotRecord.SnapshotId,
                snapshotRecord.Name,
                snapshotRecord.Sequence,
                snapshotRecord.CreatedUnixMilliseconds);
            var root = new HistoryRoot(
                new HistoryRootId(snapshot.SnapshotId.Value),
                HistoryRootKind.PersistentSnapshot,
                databaseId,
                branch.HistoryId,
                HistoryId.Empty,
                snapshot.Sequence,
                snapshot.CreatedUnixMilliseconds,
                HistoryRootState.Active);
            var expected = ToHistoryRootStoreRecord(root);
            expectedIds.Add(expected.RootId);
            if (!rootStore.TryGet(expected.RootId, out var existing) || existing is null)
            {
                rootStore.AppendCreate(expected);
                continue;
            }

            if (existing.RootState == (byte)HistoryRootState.Deleted
                || !RootMetadataMatches(existing, expected))
            {
                throw new StorageCorruptionException(
                    $"Snapshot root metadata in branch {branch.BranchId.Value} is inconsistent.");
            }
        }

        foreach (var root in rootStore.ListRetaining())
        {
            if (root.RootKind == (byte)HistoryRootKind.PersistentSnapshot
                && root.HistoryId == branch.HistoryId
                && !expectedIds.Contains(root.RootId))
            {
                rootStore.AppendDelete(root.RootId);
            }
        }
    }

    internal static void ValidateBranchGraph(
        IReadOnlyList<BranchDefinition> branches,
        HistoryId mainHistoryId,
        CommitSequence mainCurrentSequence)
    {
        var byHistory = branches.ToDictionary(branch => branch.HistoryId);
        foreach (var branch in branches)
        {
            if (branch.ParentHistoryId == mainHistoryId)
            {
                if (branch.Depth != 1 || branch.ParentBaseSequence > mainCurrentSequence)
                {
                    throw new StorageCorruptionException("Branch Main-parent metadata is invalid.");
                }
                continue;
            }

            if (!byHistory.TryGetValue(branch.ParentHistoryId, out var parent)
                || branch.Depth != checked(parent.Depth + 1)
                || branch.ParentBaseSequence > parent.LocalCurrentSequence)
            {
                throw new StorageCorruptionException("Branch ancestry or base sequence is invalid.");
            }
        }

        foreach (var branch in branches)
        {
            var seen = new HashSet<HistoryId>();
            var current = branch;
            while (current.ParentHistoryId != mainHistoryId)
            {
                if (!seen.Add(current.HistoryId)
                    || !byHistory.TryGetValue(current.ParentHistoryId, out var parent))
                {
                    throw new StorageCorruptionException("Branch ancestry contains a cycle or missing parent.");
                }
                current = parent;
            }
        }
    }

    internal static ConcurrentDictionary<BranchId, BranchRuntime> OpenBranchRuntimes(
        string databaseDirectory,
        IReadOnlyList<BranchDefinition> branches,
        PersistentBranchMetadataStore branchStore,
        PersistentHistoryRootStore rootStore,
        StorageOptions options,
        Guid databaseId,
        ResearchEventPublisher researchEvents,
        long recoveryStartedEventId)
    {
        var runtimes = new ConcurrentDictionary<BranchId, BranchRuntime>();
        try
        {
            foreach (var branch in branches)
            {
                var recoveryOperationId = Guid.NewGuid();
                var resources = new[]
                {
                    $"branch-{branch.BranchId.Value:N}-data",
                    $"branch-{branch.BranchId.Value:N}-wal",
                    "branch-catalog",
                    "history-roots",
                };
                researchEvents.TryPublish(
                    logicalEventId => new ResearchEvent(
                        logicalEventId,
                        logicalEventId,
                        ResearchEventKind.OperationStarted,
                        branch.HistoryId,
                        branch.ParentHistoryId,
                        recoveryOperationId,
                        transactionId: null,
                        resources,
                        ResearchDurabilityPhase.None,
                        branch.LocalCurrentSequence.Value,
                        recoveryStartedEventId > 0 ? [recoveryStartedEventId] : [],
                        logicalKeyId: null,
                        versionId: null,
                        offset: null,
                        bytes: null),
                    out var branchRecoveryStartedEventId);

                if (!branchStore.TryGet(branch.BranchId, out var publishedState) || publishedState is null)
                {
                    throw new StorageCorruptionException("Active branch has no persistent lifecycle state.");
                }
                var runtime = BranchRuntime.Open(
                    databaseDirectory,
                    branch,
                    publishedState,
                    branchStore.ListCommits(branch.BranchId),
                    branchStore,
                    options,
                    researchEvents,
                    branchRecoveryStartedEventId);
                ReconcileBranchSnapshotRoots(rootStore, branch, runtime.SnapshotStore.ListActive(), databaseId);
                researchEvents.TryPublish(
                    logicalEventId => new ResearchEvent(
                        logicalEventId,
                        logicalEventId,
                        ResearchEventKind.HistoryValidated,
                        branch.HistoryId,
                        branch.ParentHistoryId,
                        recoveryOperationId,
                        transactionId: null,
                        resources,
                        ResearchDurabilityPhase.AuthorityPublished,
                        runtime.Definition.LocalCurrentSequence.Value,
                        branchRecoveryStartedEventId > 0
                            ? [branchRecoveryStartedEventId]
                            : recoveryStartedEventId > 0 ? [recoveryStartedEventId] : [],
                        logicalKeyId: null,
                        versionId: null,
                        offset: null,
                        bytes: null),
                    out _);
                if (!runtimes.TryAdd(branch.BranchId, runtime))
                {
                    runtime.Dispose();
                    throw new StorageCorruptionException("Branch runtime identity is duplicated.");
                }
            }
            return runtimes;
        }
        catch
        {
            foreach (var runtime in runtimes.Values)
            {
                runtime.Dispose();
            }
            throw;
        }
    }

    internal static void ValidateRecoveredHistoryRoots(
        HistoryRootRegistry roots,
        BranchCatalog branches,
        HistoryId mainHistoryId,
        CommitSequence mainCurrentSequence,
        Guid databaseId)
    {
        foreach (var root in roots.ListActive())
        {
            if (root.OwnerDatabaseId != databaseId)
            {
                throw new StorageCorruptionException("Historical root belongs to another database identity.");
            }

            switch (root.Kind)
            {
                case HistoryRootKind.PersistentSnapshot:
                    if (root.HistoryId == mainHistoryId)
                    {
                        if (root.Boundary > mainCurrentSequence)
                        {
                            throw new StorageCorruptionException("Main snapshot root references future history.");
                        }
                    }
                    else if (!branches.TryGetByHistory(root.HistoryId, out var branch)
                             || branch is null
                             || root.Boundary > branch.LocalCurrentSequence)
                    {
                        throw new StorageCorruptionException("Branch snapshot root references an unknown or future history.");
                    }
                    break;
                case HistoryRootKind.BranchBase:
                    if (!branches.TryGetByHistory(root.HistoryId, out var child)
                        || child is null
                        || child.BaseRootId != root.RootId
                        || child.ParentHistoryId != root.ParentHistoryId
                        || child.ParentBaseSequence != root.Boundary)
                    {
                        throw new StorageCorruptionException("Branch-base root is not owned by its declared child history.");
                    }
                    break;
                default:
                    throw new StorageCorruptionException(
                        $"Persistent root kind {root.Kind} is not valid in the current history-root model.");
            }
        }
    }

    private static bool RootMetadataMatches(HistoryRootStoreRecord left, HistoryRootStoreRecord right)
        => left.RootId == right.RootId
           && left.RootKind == right.RootKind
           && left.OwnerDatabaseId == right.OwnerDatabaseId
           && left.HistoryId == right.HistoryId
           && left.ParentHistoryId == right.ParentHistoryId
           && left.Boundary == right.Boundary
           && left.CreatedUnixMilliseconds == right.CreatedUnixMilliseconds;
}
