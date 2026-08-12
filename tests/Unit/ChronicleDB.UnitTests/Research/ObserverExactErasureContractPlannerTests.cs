using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ObserverExactErasureContractPlannerTests
{
    [Fact]
    public void AlreadyDeletedCurrentStateRequestCanRewriteResidualRepresentationsWithoutRevocation()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot(
            [History(
                history,
                2,
                2,
                Version(history, "K", 1, 32),
                Version(history, "K", 2, 0, tombstone: true))],
            []);
        var closure = Closure(
            "K",
            retention,
            scanComplete: true,
            Representation("mvcc", ErasureRepresentationKind.MvccVersion, history, value: true),
            Representation("wal", ErasureRepresentationKind.WalMutation, history, value: true),
            Representation("derived", ErasureRepresentationKind.DerivedCurrentState, history, value: true));

        var plan = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Request);

        Assert.Equal(ObserverExactErasurePlanOutcome.RequestAllowed, plan.Outcome);
        Assert.True(plan.ExecutableWithExistingSemantics);
        Assert.True(plan.CanAcknowledgeAfterDurablePlanApplied);
        Assert.Equal(0, plan.KeyScopedSemanticExtensionActionCount);
        Assert.Empty(plan.BlockingHistoricalObserverIds);
        Assert.DoesNotContain(plan.SemanticActions, action =>
            action.Kind == ObserverExactErasureActionKind.DeleteOrTombstoneCurrentState);
        Assert.Contains(plan.RepresentationActions, action =>
            action.Kind == ObserverExactErasureActionKind.RewriteRecoveryRepresentation
            && action.RepresentationIds.Contains("wal", StringComparer.Ordinal));
        Assert.Contains(plan.RepresentationActions, action =>
            action.Kind == ObserverExactErasureActionKind.ReclaimPhysicalRepresentation
            && action.RepresentationIds.Contains("derived", StringComparer.Ordinal));
    }

    [Fact]
    public void LiveCurrentValueIsAlsoGenericTimeTravelBlockerAtSameBoundary()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot(
            [History(history, 1, 1, Version(history, "K", 1, 32))],
            []);
        var closure = Closure(
            "K",
            retention,
            scanComplete: true,
            Representation("mvcc", ErasureRepresentationKind.MvccVersion, history, value: true));

        var request = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Request);
        var force = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        Assert.Equal(ObserverExactErasurePlanOutcome.BlockedByObserverContract, request.Outcome);
        Assert.Equal(ObserverExactErasurePlanOutcome.ForcePlanRequiresKeyScopedSemanticExtension, force.Outcome);
        Assert.Contains(force.SemanticActions, action => action.Kind == ObserverExactErasureActionKind.DeleteOrTombstoneCurrentState);
        var generic = Assert.Single(force.SemanticActions, action => action.Kind == ObserverExactErasureActionKind.RevokeGenericTimeTravelForKey);
        Assert.Equal((ulong)1, generic.MinimumBoundary);
        Assert.Equal((ulong)1, generic.MaximumBoundary);
    }

    [Fact]
    public void GenericHistoryAndSnapshotRequireKeyScopedExtensionInsteadOfPretendingExistingMechanismsAreMinimal()
    {
        var history = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var retention = Snapshot(
            [History(
                history,
                1,
                2,
                Version(history, "K", 1, 32),
                Version(history, "K", 2, 0, tombstone: true))],
            [PersistentSnapshot(snapshotId, history, 1)]);
        var closure = Closure(
            "K",
            retention,
            scanComplete: true,
            Representation("old-version", ErasureRepresentationKind.MvccVersion, history, value: true));

        var request = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Request);
        var force = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        Assert.Equal(ObserverExactErasurePlanOutcome.BlockedByObserverContract, request.Outcome);
        Assert.Equal(ObserverExactErasurePlanOutcome.ForcePlanRequiresKeyScopedSemanticExtension, force.Outcome);
        Assert.False(force.ExecutableWithExistingSemantics);
        Assert.False(force.CanAcknowledgeAfterDurablePlanApplied);
        Assert.Equal(2, force.KeyScopedSemanticExtensionActionCount);
        Assert.Equal(2, force.CollateralWholeObserverAlternativeCount);
        var generic = Assert.Single(force.SemanticActions, action =>
            action.Kind == ObserverExactErasureActionKind.RevokeGenericTimeTravelForKey);
        Assert.Equal(ObserverExactErasureExistingAlternative.AdvanceWholeHistoryRetentionFloor, generic.ExistingAlternative);
        Assert.True(generic.RequiresKeyScopedSemanticExtension);
        var snapshot = Assert.Single(force.SemanticActions, action =>
            action.Kind == ObserverExactErasureActionKind.RevokePersistentSnapshotForKey);
        Assert.Equal(ObserverExactErasureExistingAlternative.DeleteWholeSnapshot, snapshot.ExistingAlternative);
        Assert.True(snapshot.RequiresKeyScopedSemanticExtension);
    }

    [Fact]
    public void ActiveHistoricalBlockerCanBeHandledByQuiescenceWithoutNewKeyScopedSemantics()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot(
            [History(
                history,
                2,
                2,
                Version(history, "K", 1, 32),
                Version(history, "K", 2, 0, tombstone: true))],
            [],
            [new ResearchActiveRetentionBoundarySnapshot(history, 1)]);
        var closure = Closure(
            "K",
            retention,
            scanComplete: true,
            Representation("checkpoint", ErasureRepresentationKind.CheckpointVersion, history, value: true));

        var request = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Request);
        var force = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        Assert.Equal(ObserverExactErasurePlanOutcome.BlockedByObserverContract, request.Outcome);
        Assert.Equal(ObserverExactErasurePlanOutcome.ForcePlanReadyWithExistingSemantics, force.Outcome);
        Assert.True(force.ExecutableWithExistingSemantics);
        Assert.True(force.RequiresQuiescence);
        Assert.Equal(0, force.KeyScopedSemanticExtensionActionCount);
        var active = Assert.Single(force.SemanticActions, action =>
            action.Kind == ObserverExactErasureActionKind.WaitForActiveObserverRelease);
        Assert.Equal(ObserverExactErasureExistingAlternative.WaitForObserverRelease, active.ExistingAlternative);
        Assert.False(active.RequiresKeyScopedSemanticExtension);
    }

    [Fact]
    public void IncompleteRepresentationClosureFailsClosedForRequestAndAuthorizedForce()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot(
            [History(history, 1, 1, Version(history, "K", 1, 32))],
            []);
        var closure = Closure(
            "K",
            retention,
            scanComplete: false,
            Representation("mvcc", ErasureRepresentationKind.MvccVersion, history, value: true));

        var request = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Request);
        var force = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        Assert.Equal(ObserverExactErasurePlanOutcome.BlockedByIncompleteClosure, request.Outcome);
        Assert.Equal(ObserverExactErasurePlanOutcome.BlockedByIncompleteClosure, force.Outcome);
        Assert.False(request.CanAcknowledgeAfterDurablePlanApplied);
        Assert.False(force.CanAcknowledgeAfterDurablePlanApplied);
    }

    [Fact]
    public void ForceRequiresExplicitAuthorizationBeforeAnyDestructivePlanIsReady()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot(
            [History(history, 1, 1, Version(history, "K", 1, 32))],
            []);
        var closure = Closure("K", retention, scanComplete: true);

        var force = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force);

        Assert.Equal(ObserverExactErasurePlanOutcome.ForceAuthorizationRequired, force.Outcome);
        Assert.False(force.CanAcknowledgeAfterDurablePlanApplied);
    }

    [Fact]
    public void NestedInheritedSnapshotActionTargetsObserverContractNotBranchBaseEdge()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var retention = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 32)),
                History(child, 0, 0),
            ],
            [
                BranchBase(child, main, 1),
                PersistentSnapshot(snapshotId, child, 0),
            ]);
        var closure = Closure(
            "K",
            retention,
            scanComplete: true,
            Representation("main-old", ErasureRepresentationKind.MvccVersion, main, value: true));

        var force = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        var snapshot = Assert.Single(force.SemanticActions, action =>
            action.Kind == ObserverExactErasureActionKind.RevokePersistentSnapshotForKey);
        Assert.Equal(child, snapshot.HistoryId);
        Assert.Contains($"root:{snapshotId:N}", snapshot.ObserverIds);
        Assert.DoesNotContain(force.SemanticActions, action => action.ActionId.Contains("BranchBase", StringComparison.Ordinal));
        Assert.Contains(force.SemanticAnalysis.InheritedBlockingObservers, item =>
            item.ObserverId == $"root:{snapshotId:N}" && item.ResolvedHistoryId == main);
    }

    [Fact]
    public void RepresentationTopologyMissingObserverHistoryFailsClosed()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot([History(history, 0, 0)], []);
        var closure = new ErasureClosureInput(
            "K",
            Guid.NewGuid(),
            [new ErasureHistoryNode(Guid.NewGuid(), null)],
            [],
            PhysicalRepresentationScanComplete: true,
            []);

        Assert.Throws<ArgumentException>(() =>
            ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Analyze));
    }

    private static ErasureClosureInput Closure(
        string keyId,
        ResearchRetentionSnapshot retention,
        bool scanComplete,
        params ErasureRepresentation[] representations)
    {
        var edges = retention.PersistentRoots
            .Where(root => root.Kind == "BranchBase")
            .ToDictionary(root => root.OwnerHistoryId, root => root.ProtectedHistoryId);
        var topology = retention.Histories
            .Select(history => new ErasureHistoryNode(
                history.HistoryId,
                edges.TryGetValue(history.HistoryId, out var parent) ? parent : null))
            .ToArray();
        return new ErasureClosureInput(
            keyId,
            retention.Histories[0].HistoryId,
            topology,
            representations,
            scanComplete,
            scanComplete ? [] : ["unscanned-test-generation"]);
    }

    private static ErasureRepresentation Representation(
        string id,
        ErasureRepresentationKind kind,
        Guid historyId,
        bool value,
        bool observer = false)
        => new(
            id,
            kind,
            historyId,
            historyId,
            Sequence: 1,
            value ? ErasureContentState.Value : ErasureContentState.Tombstone,
            observer);

    private static ResearchRetentionSnapshot Snapshot(
        IReadOnlyList<ResearchHistoryRetentionSnapshot> histories,
        IReadOnlyList<ResearchPersistentRetentionRootSnapshot> roots,
        IReadOnlyList<ResearchActiveRetentionBoundarySnapshot>? active = null)
        => new(histories, roots, active ?? []);

    private static ResearchHistoryRetentionSnapshot History(
        Guid id,
        ulong floor,
        ulong current,
        params ResearchCommittedVersionSnapshot[] versions)
        => new(id, floor, current, versions);

    private static ResearchCommittedVersionSnapshot Version(
        Guid historyId,
        string key,
        ulong sequence,
        int bytes,
        bool tombstone = false)
        => new(
            $"{historyId:N}:{key}:{sequence}",
            Guid.NewGuid(),
            sequence,
            key,
            KeyBytes: 8,
            ValueBytes: bytes,
            IsTombstone: tombstone);

    private static ResearchPersistentRetentionRootSnapshot BranchBase(Guid child, Guid parent, ulong boundary)
        => new(Guid.NewGuid(), "BranchBase", child, parent, boundary);

    private static ResearchPersistentRetentionRootSnapshot PersistentSnapshot(Guid rootId, Guid history, ulong boundary)
        => new(rootId, "PersistentSnapshot", history, history, boundary);
}
