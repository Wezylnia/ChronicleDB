using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ObserverExactErasureOracleTests
{
    [Fact]
    public void NestedSnapshotResolvesInheritedMainValueAcrossTwoBranchEdges()
    {
        var main = Guid.NewGuid();
        var a = Guid.NewGuid();
        var a1 = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var input = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 100)),
                History(a, 0, 0),
                History(a1, 0, 0),
            ],
            [
                BranchBase(a, main, 1),
                BranchBase(a1, a, 0),
                PersistentSnapshot(snapshotId, a1, 0),
            ]);

        var result = new ObserverExactErasureOracle(input).Analyze("K");

        var witness = Assert.Single(result.BlockingObservers, item => item.ObserverId == RootObserverId(snapshotId));
        Assert.Equal(ErasureContentState.Value, witness.Content);
        Assert.Equal(main, witness.ResolvedHistoryId);
        Assert.Equal(2, witness.ParentFallbackHops);
        Assert.Contains(RootObserverId(snapshotId), result.BlockingObserverIdsUnrepresentedByLegacyP6);
        var legacy = Assert.Single(result.LegacyLocalRootClassifications, item => item.RootId == snapshotId);
        Assert.Equal(ErasureContentState.Absent, legacy.Content);
    }

    [Fact]
    public void TombstoneStopsFallbackToParentValue()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var nested = Guid.NewGuid();
        var input = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 100)),
                History(child, 1, 1, Version(child, "K", 1, 0, tombstone: true)),
                History(nested, 0, 0),
            ],
            [
                BranchBase(child, main, 1),
                BranchBase(nested, child, 1),
            ]);

        var result = new ObserverExactErasureOracle(input).Analyze("K");
        var nestedCurrent = Assert.Single(result.Observers, item =>
            item.Kind == ErasureObserverContractKind.CurrentState && item.HistoryId == nested);

        Assert.Equal(ErasureContentState.Tombstone, nestedCurrent.Content);
        Assert.Equal(child, nestedCurrent.ResolvedHistoryId);
        Assert.Equal(1, nestedCurrent.ParentFallbackHops);
        Assert.DoesNotContain(result.BlockingObservers, item => item.HistoryId == nested);
    }

    [Fact]
    public void LocalValueShadowsParentForChildObserver()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var input = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 100)),
                History(child, 1, 1, Version(child, "K", 1, 25)),
            ],
            [BranchBase(child, main, 1)]);

        var result = new ObserverExactErasureOracle(input).Analyze("K");
        var current = Assert.Single(result.BlockingObservers, item =>
            item.Kind == ErasureObserverContractKind.CurrentState && item.HistoryId == child);

        Assert.Equal(child, current.ResolvedHistoryId);
        Assert.Equal(0, current.ParentFallbackHops);
        Assert.Equal(VersionId(child, "K", 1), current.ResolvedVersionId);
    }

    [Fact]
    public void PreShadowActiveBoundaryFallsBackWhilePostShadowCurrentStopsLocally()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var input = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 100)),
                History(child, 0, 2, Version(child, "K", 2, 20)),
            ],
            [BranchBase(child, main, 1)],
            [new ResearchActiveRetentionBoundarySnapshot(child, 1)]);

        var result = new ObserverExactErasureOracle(input).Analyze("K");
        var active = Assert.Single(result.BlockingObservers, item => item.Kind == ErasureObserverContractKind.ActiveBoundary);
        var current = Assert.Single(result.BlockingObservers, item =>
            item.Kind == ErasureObserverContractKind.CurrentState && item.HistoryId == child);

        Assert.Equal(main, active.ResolvedHistoryId);
        Assert.Equal(1, active.ParentFallbackHops);
        Assert.Equal(child, current.ResolvedHistoryId);
        Assert.Equal(0, current.ParentFallbackHops);
    }

    [Fact]
    public void GenericTimeTravelBlocksValueWithoutAnyPersistentRoot()
    {
        var main = Guid.NewGuid();
        var input = Snapshot(
            [History(
                main,
                floor: 1,
                current: 3,
                Version(main, "K", 1, 100),
                Version(main, "K", 3, 0, tombstone: true))],
            []);

        var result = new ObserverExactErasureOracle(input).Analyze("K");

        var historical = Assert.Single(result.BlockingObservers, item =>
            item.Kind == ErasureObserverContractKind.GenericTimeTravel
            && item.HistoryId == main
            && item.Boundary == 1);
        Assert.Equal(ErasureContentState.Value, historical.Content);
        Assert.Empty(result.LegacyLocalRootBlockers);
        Assert.Contains(historical.ObserverId, result.BlockingObserverIdsUnrepresentedByLegacyP6);
    }

    [Fact]
    public void BranchBaseIsNotUnconditionalBlockerWhenChildNeverFallsBackForKey()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var baseRoot = Guid.NewGuid();
        var input = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 100)),
                History(child, 1, 1, Version(child, "K", 1, 50)),
            ],
            [BranchBase(baseRoot, child, main, 1)]);

        var result = new ObserverExactErasureOracle(input).Analyze("K");

        Assert.Contains(result.LegacyLocalRootBlockers, item => item.RootId == baseRoot);
        Assert.Contains(baseRoot, result.BranchBaseFalsePositiveRootIds);
        Assert.DoesNotContain(result.BlockingObservers, item =>
            item.HistoryId == child && item.ParentFallbackHops > 0);
    }

    [Fact]
    public void AncestorEdgeNeededByNestedObserverIsNotReportedAsFalsePositive()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var nested = Guid.NewGuid();
        var childBase = Guid.NewGuid();
        var input = Snapshot(
            [
                History(main, 1, 1, Version(main, "K", 1, 100)),
                History(child, 0, 0),
                History(nested, 0, 0),
            ],
            [
                BranchBase(childBase, child, main, 1),
                BranchBase(nested, child, 0),
            ]);

        var result = new ObserverExactErasureOracle(input).Analyze("K");

        Assert.DoesNotContain(childBase, result.BranchBaseFalsePositiveRootIds);
        Assert.Contains(result.InheritedBlockingObservers, item =>
            item.HistoryId == nested && item.ResolvedHistoryId == main && item.ParentFallbackHops == 2);
    }

    [Fact]
    public void CyclicBranchEdgesFailClosed()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var input = Snapshot(
            [History(first, 0, 0), History(second, 0, 0)],
            [BranchBase(first, second, 0), BranchBase(second, first, 0)]);

        Assert.Throws<ArgumentException>(() => new ObserverExactErasureOracle(input));
    }

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
            VersionId(historyId, key, sequence),
            Guid.NewGuid(),
            sequence,
            key,
            KeyBytes: 8,
            ValueBytes: bytes,
            IsTombstone: tombstone);

    private static string VersionId(Guid historyId, string key, ulong sequence)
        => $"{historyId:N}:{key}:{sequence}";

    private static ResearchPersistentRetentionRootSnapshot BranchBase(Guid child, Guid parent, ulong boundary)
        => BranchBase(Guid.NewGuid(), child, parent, boundary);

    private static ResearchPersistentRetentionRootSnapshot BranchBase(Guid rootId, Guid child, Guid parent, ulong boundary)
        => new(rootId, "BranchBase", child, parent, boundary);

    private static ResearchPersistentRetentionRootSnapshot PersistentSnapshot(Guid rootId, Guid history, ulong boundary)
        => new(rootId, "PersistentSnapshot", history, history, boundary);

    private static string RootObserverId(Guid rootId) => $"root:{rootId:N}";
}
