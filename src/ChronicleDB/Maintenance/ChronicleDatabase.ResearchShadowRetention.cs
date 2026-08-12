using System.Diagnostics;
using System.Security.Cryptography;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Transactions.Mvcc;

namespace ChronicleDB;

public sealed partial class ChronicleDatabase
{
    /// <summary>
    /// Research-only A1 prototype. Computes a cross-history shadow-aware retained
    /// projection and publishes it descendant-first so any child floor advancement
    /// that justifies dropping a parent predecessor becomes durable before that
    /// predecessor is removed from the parent's recovery authority.
    ///
    /// The default production GC never calls this method.
    /// </summary>
    public ShadowAwareGarbageCollectionResult RunShadowAwareGarbageCollection(
        GarbageCollectionOptions? options = null)
    {
        options ??= new GarbageCollectionOptions();
        options.Validate();
        if (!options.IncludeBranches)
        {
            throw new ArgumentException(
                "Shadow-aware cross-history GC requires branch processing to remain enabled.",
                nameof(options));
        }

        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                // Lock commit gates in the same canonical BranchId order as normal
                // maintenance. Processing order is independent and is descendant-first.
                var lockOrder = _branchRuntimes.Values
                    .OrderBy(runtime => runtime.Definition.BranchId.Value)
                    .ToArray();
                var acquired = AcquireMaintenanceCommitGates(lockOrder);
                try
                {
                    var processingOrder = _branchRuntimes.Values
                        .OrderByDescending(runtime => runtime.Definition.Depth)
                        .ThenBy(runtime => runtime.Definition.HistoryId.Value)
                        .ToArray();

                    var mainCurrent = GetCurrentCommitSequence();
                    var mainCurrentFloor = GetHistoryRetentionFloor();
                    var mainTargetFloor = ComputeTargetFloor(
                        mainCurrentFloor,
                        mainCurrent,
                        options.RetainRecentCommits);

                    var targetFloors = new Dictionary<Guid, CommitSequence>
                    {
                        [_mainHistoryId.Value] = mainTargetFloor,
                    };
                    foreach (var runtime in processingOrder)
                    {
                        targetFloors[runtime.Definition.HistoryId.Value] = ComputeTargetFloor(
                            runtime.HistoryFloor,
                            runtime.Definition.LocalCurrentSequence,
                            options.RetainRecentCommits);
                    }

                    var rawHistories = new Dictionary<Guid, IReadOnlyList<CommittedVersionSnapshot>>
                    {
                        [_mainHistoryId.Value] = _versions.SnapshotHistory(),
                    };
                    foreach (var runtime in processingOrder)
                    {
                        rawHistories[runtime.Definition.HistoryId.Value] = runtime.Versions.SnapshotHistory();
                    }

                    var researchHistories = new List<ResearchHistoryRetentionSnapshot>(rawHistories.Count)
                    {
                        CaptureHistory(
                            _mainHistoryId.Value,
                            mainTargetFloor,
                            mainCurrent,
                            rawHistories[_mainHistoryId.Value]),
                    };
                    researchHistories.AddRange(processingOrder.Select(runtime => CaptureHistory(
                        runtime.Definition.HistoryId.Value,
                        targetFloors[runtime.Definition.HistoryId.Value],
                        runtime.Definition.LocalCurrentSequence,
                        rawHistories[runtime.Definition.HistoryId.Value])));

                    var roots = _historyRoots.ListActive()
                        .OrderBy(root => root.RootId.Value)
                        .Select(root => new ResearchPersistentRetentionRootSnapshot(
                            root.RootId.Value,
                            root.Kind.ToString(),
                            root.HistoryId.Value,
                            root.ProtectedHistoryId.Value,
                            root.Boundary.Value))
                        .ToArray();
                    var active = researchHistories
                        .SelectMany(history => _activeHistoryBoundaries
                            .ListBoundaries(new HistoryId(history.HistoryId))
                            .Select(boundary => new ResearchActiveRetentionBoundarySnapshot(
                                history.HistoryId,
                                boundary.Value)))
                        .OrderBy(item => item.ProtectedHistoryId)
                        .ThenBy(item => item.Boundary)
                        .ToArray();

                    var snapshot = new ResearchRetentionSnapshot(researchHistories, roots, active);
                    var projectionStarted = Stopwatch.GetTimestamp();
                    var analysis = new ShadowAwareRetentionProjection(snapshot).Analyze();
                    var projectionAnalysisMilliseconds = Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds;
                    if (!analysis.CandidateIsSubsetOfBaseline
                        || !analysis.ObserverEquivalenceVerified
                        || !analysis.ObserverMinimalityVerified)
                    {
                        throw new InvalidOperationException(
                            "Shadow-aware research projection failed its subset/equivalence/minimality gate.");
                    }

                    var requiredIds = analysis.RequiredVersionIds.ToHashSet(StringComparer.Ordinal);
                    var matchedIds = new HashSet<string>(StringComparer.Ordinal);
                    var projections = new Dictionary<Guid, IReadOnlyList<CommittedVersionSnapshot>>();
                    foreach (var (historyId, history) in rawHistories)
                    {
                        var projection = history
                            .Where(version =>
                            {
                                var id = CreateResearchVersionId(historyId, version);
                                if (!requiredIds.Contains(id))
                                {
                                    return false;
                                }

                                matchedIds.Add(id);
                                return true;
                            })
                            .OrderBy(version => version.CommitSequence.Value)
                            .ThenBy(version => version.Key, Core.Keys.BinaryKeyLexicographicComparer.Instance)
                            .ToArray();
                        projections[historyId] = projection;
                    }

                    if (!matchedIds.SetEquals(requiredIds))
                    {
                        throw new InvalidOperationException(
                            "Shadow-aware projection contains a logical version that cannot be mapped back to engine history.");
                    }

                    var historiesProcessed = 0;
                    var reclaimedVersions = 0;
                    long checkpointBytes = 0;
                    var publishedOrder = new List<Guid>(processingOrder.Length + 1);

                    // Descendants first. A parent projection may rely on a descendant's
                    // target floor no longer exposing a pre-shadow branch state.
                    foreach (var runtime in processingOrder)
                    {
                        var historyId = runtime.Definition.HistoryId.Value;
                        var targetFloor = targetFloors[historyId];
                        var projection = projections[historyId];
                        var floorAdvances = targetFloor > runtime.HistoryFloor;
                        var projectionShrinks = projection.Count < runtime.Versions.VersionCount;
                        if (!floorAdvances && !projectionShrinks)
                        {
                            continue;
                        }

                        checkpointBytes = checked(checkpointBytes + PublishBranchHistoryCheckpoint(
                            runtime,
                            runtime.Definition.LocalCurrentSequence,
                            targetFloor,
                            projection));
                        var compacted = runtime.Versions.CompactHistoryToProjection(projection);
                        reclaimedVersions = checked(reclaimedVersions + compacted.ReclaimedVersions);
                        if (floorAdvances)
                        {
                            runtime.AdvanceHistoryFloor(targetFloor);
                            _historyRoots.AdvanceHistoryFloor(runtime.Definition.HistoryId, targetFloor);
                        }

                        historiesProcessed++;
                        publishedOrder.Add(historyId);
                    }

                    var mainProjection = projections[_mainHistoryId.Value];
                    var mainFloorAdvances = mainTargetFloor > mainCurrentFloor;
                    var mainProjectionShrinks = mainProjection.Count < _versions.VersionCount;
                    if (mainFloorAdvances || mainProjectionShrinks)
                    {
                        checkpointBytes = checked(checkpointBytes + PublishMainHistoryCheckpoint(
                            mainCurrent,
                            mainTargetFloor,
                            mainProjection));
                        var compacted = _versions.CompactHistoryToProjection(mainProjection);
                        reclaimedVersions = checked(reclaimedVersions + compacted.ReclaimedVersions);
                        if (mainFloorAdvances)
                        {
                            _historyRoots.AdvanceHistoryFloor(_mainHistoryId, mainTargetFloor);
                            _snapshots.AdvanceRetentionFloor(mainTargetFloor, mainCurrent);
                        }

                        historiesProcessed++;
                        publishedOrder.Add(_mainHistoryId.Value);
                    }

                    // Keep lifecycle cleanup behavior aligned with normal GC without
                    // changing the research projection or default production policy.
                    var deletedDirectories = ReclaimDeletedBranchDirectories();
                    _snapshotStore.CompactJournal();
                    foreach (var runtime in lockOrder)
                    {
                        runtime.SnapshotStore.CompactJournal();
                    }
                    _historyRootStore.CompactJournal();
                    _historyRoots.PruneDeleted();
                    if (deletedDirectories.Pending == 0)
                    {
                        _branchStore.CompactJournal();
                    }

                    return new ShadowAwareGarbageCollectionResult(
                        HistoriesProcessed: historiesProcessed,
                        ReclaimedVersions: reclaimedVersions,
                        CheckpointBytes: checkpointBytes,
                        BaselinePayloadBytes: analysis.BaselinePayloadBytes,
                        RetainedPayloadBytes: analysis.ShadowAwarePayloadBytes,
                        ShadowReleasedPayloadBytes: analysis.ShadowReleasedPayloadBytes,
                        ShadowAwareReclamationRatio: analysis.ShadowAwareReclamationRatio,
                        ObserverEquivalenceCheckCount: analysis.ObserverEquivalenceCheckCount,
                        ProjectionAnalysisMilliseconds: projectionAnalysisMilliseconds,
                        MainRetentionFloor: mainTargetFloor.Value,
                        PublishedHistoryOrder: Array.AsReadOnly(publishedOrder.ToArray()),
                        DeletedBranchDirectories: deletedDirectories.Reclaimed,
                        PendingDeletedBranchDirectories: deletedDirectories.Pending);
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

    private static string CreateResearchVersionId(Guid historyId, CommittedVersionSnapshot version)
    {
        var keyBytes = version.Key.ToArray();
        var keyId = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{historyId:N}:{version.CommitSequence.Value}:{version.TransactionId.Value:N}:{keyId}");
    }
}

public sealed record ShadowAwareGarbageCollectionResult(
    int HistoriesProcessed,
    int ReclaimedVersions,
    long CheckpointBytes,
    long BaselinePayloadBytes,
    long RetainedPayloadBytes,
    long ShadowReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    int ObserverEquivalenceCheckCount,
    double ProjectionAnalysisMilliseconds,
    ulong MainRetentionFloor,
    IReadOnlyList<Guid> PublishedHistoryOrder,
    int DeletedBranchDirectories,
    int PendingDeletedBranchDirectories);
