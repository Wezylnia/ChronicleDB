using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ObserverScopedErasureAuthorityDescriptorTests
{
    [Fact]
    public void CompilerBindsNestedInheritedWitnessInsteadOfBranchBaseEdge()
    {
        var main = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var child = Guid.Parse("00000000-0000-0000-0000-000000000022");
        var snapshotId = Guid.Parse("00000000-0000-0000-0000-000000000033");
        var retention = Snapshot(
            [History(main, 1, 1, Version(main, "K", 1, 32)), History(child, 0, 0)],
            [BranchBase(child, main, 1), PersistentSnapshot(snapshotId, child, 0)]);
        var closure = Closure(
            "K",
            retention,
            Representation("checkpoint", ErasureRepresentationKind.CheckpointVersion, main, value: true),
            Representation("derived", ErasureRepresentationKind.DerivedCurrentState, main, value: true));
        var plan = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        var descriptor = ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan);

        var snapshotRevocation = Assert.Single(descriptor.Revocations, item => item.ObserverId == $"root:{snapshotId:N}");
        Assert.Equal(child, snapshotRevocation.HistoryId);
        Assert.Equal(main, snapshotRevocation.ResolvedHistoryId);
        Assert.Equal(1UL, snapshotRevocation.ResolvedSequence);
        Assert.Equal(1, snapshotRevocation.ParentFallbackHops);
        Assert.DoesNotContain(descriptor.Revocations, item => item.ObserverId.Contains("BranchBase", StringComparison.Ordinal));
        Assert.Contains("checkpoint", descriptor.RewriteRepresentationIds);
        Assert.Contains("derived", descriptor.ReclaimRepresentationIds);
    }

    [Fact]
    public void CompilerPreservesNonValueObserversAndProducesStableCanonicalHash()
    {
        var history = Guid.Parse("00000000-0000-0000-0000-000000000044");
        var snapshotId = Guid.Parse("00000000-0000-0000-0000-000000000055");
        var retention = Snapshot(
            [History(
                history,
                1,
                2,
                Version(history, "K", 1, 32),
                Version(history, "K", 2, 0, tombstone: true))],
            [PersistentSnapshot(snapshotId, history, 2)]);
        var closure = Closure("K", retention, Representation("wal", ErasureRepresentationKind.WalMutation, history, value: true));
        var plan = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        var first = ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan);
        var second = ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan);

        Assert.Equal(first.CanonicalSha256, second.CanonicalSha256);
        Assert.Equal(64, first.CanonicalSha256.Length);
        Assert.Contains(first.PreservedTargetObservations, item =>
            item.Content == ErasureContentState.Tombstone && item.Boundary == 2);
        Assert.DoesNotContain(first.Revocations, item =>
            first.PreservedTargetObservations.Any(preserved => preserved.ObserverId == item.ObserverId));
    }


    [Fact]
    public void VisibilityRegionsCoverFutureObserversBetweenSemanticChangePointsWithoutMaskingTombstoneGaps()
    {
        var history = Guid.Parse("00000000-0000-0000-0000-000000000066");
        var retention = Snapshot(
            [History(
                history,
                1,
                6,
                Version(history, "K", 1, 32),
                Version(history, "K", 3, 0, tombstone: true),
                Version(history, "K", 5, 32))],
            []);
        var closure = Closure("K", retention, Representation("wal", ErasureRepresentationKind.WalMutation, history, value: true));
        var plan = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);
        var descriptor = ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan);

        Assert.Contains(descriptor.VisibilityRegions, region =>
            region.HistoryId == history && region.MinimumBoundary == 1 && region.MaximumBoundary == 2);
        Assert.Contains(descriptor.VisibilityRegions, region =>
            region.HistoryId == history && region.MinimumBoundary == 5 && region.MaximumBoundary == 6);

        Assert.Equal(
            ObserverScopedErasureReadDecision.RedactTargetValue,
            ObserverScopedErasureAuthorityReadFilter.Evaluate(descriptor, "K", history, 2));
        Assert.Equal(
            ObserverScopedErasureReadDecision.PassThrough,
            ObserverScopedErasureAuthorityReadFilter.Evaluate(descriptor, "K", history, 3));
        Assert.Equal(
            ObserverScopedErasureReadDecision.RedactTargetValue,
            ObserverScopedErasureAuthorityReadFilter.Evaluate(descriptor, "K", history, 6));
        Assert.Equal(
            ObserverScopedErasureReadDecision.PassThrough,
            ObserverScopedErasureAuthorityReadFilter.Evaluate(descriptor, "OTHER", history, 2));
    }

    [Fact]
    public void CompilerRejectsPlansThatDoNotRequireObserverScopedExtension()
    {
        var history = Guid.NewGuid();
        var retention = Snapshot(
            [History(history, 1, 1, Version(history, "K", 1, 0, tombstone: true))],
            []);
        var closure = Closure("K", retention);
        var plan = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);

        Assert.Throws<ArgumentException>(() => ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan));
    }

    private static ErasureClosureInput Closure(
        string keyId,
        ResearchRetentionSnapshot retention,
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
            PhysicalRepresentationScanComplete: true,
            []);
    }

    private static ErasureRepresentation Representation(
        string id,
        ErasureRepresentationKind kind,
        Guid historyId,
        bool value)
        => new(id, kind, historyId, historyId, 1, value ? ErasureContentState.Value : ErasureContentState.Tombstone, false);

    private static ResearchRetentionSnapshot Snapshot(
        IReadOnlyList<ResearchHistoryRetentionSnapshot> histories,
        IReadOnlyList<ResearchPersistentRetentionRootSnapshot> roots)
        => new(histories, roots, []);

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
            Guid.Parse("00000000-0000-0000-0000-000000000099"),
            sequence,
            key,
            8,
            bytes,
            tombstone);

    private static ResearchPersistentRetentionRootSnapshot BranchBase(Guid child, Guid parent, ulong boundary)
        => new(Guid.Parse("00000000-0000-0000-0000-000000000077"), "BranchBase", child, parent, boundary);

    private static ResearchPersistentRetentionRootSnapshot PersistentSnapshot(Guid rootId, Guid history, ulong boundary)
        => new(rootId, "PersistentSnapshot", history, history, boundary);
}
