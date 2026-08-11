using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class CoarseRetentionBaselineTests
{
    [Fact]
    public void OldestRootHorizonPinsIntermediateVersionsThatExactRootDoesNotNeed()
    {
        var historyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var rootId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var snapshot = new ResearchRetentionSnapshot(
            [new ResearchHistoryRetentionSnapshot(
                historyId,
                RetentionFloor: 4,
                CurrentSequence: 4,
                [Version("v1", 1), Version("v2", 2), Version("v3", 3), Version("v4", 4)])],
            [new ResearchPersistentRetentionRootSnapshot(rootId, "BranchBase", Guid.NewGuid(), historyId, Boundary: 1)],
            []);

        var coarse = CoarseOldestRootRetentionAnalyzer.Analyze(snapshot);
        var exact = new RetentionInspector(snapshot).WhatIfDrop(rootId);

        Assert.Equal(40, coarse.PayloadBytesWithPersistentRoots);
        Assert.Equal(10, coarse.PayloadBytesWithoutPersistentRoots);
        Assert.Equal(30, coarse.RootInducedPayloadBytes);
        Assert.Equal(3, coarse.RootInducedVersionCount);
        Assert.Equal(10, exact.MarginalPayloadBytes);
    }

    [Fact]
    public void NoPersistentRootAddsNoCoarseRetentionDebt()
    {
        var historyId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var snapshot = new ResearchRetentionSnapshot(
            [new ResearchHistoryRetentionSnapshot(
                historyId,
                RetentionFloor: 3,
                CurrentSequence: 3,
                [Version("v1", 1), Version("v2", 2), Version("v3", 3)])],
            [],
            []);

        var coarse = CoarseOldestRootRetentionAnalyzer.Analyze(snapshot);

        Assert.Equal(0, coarse.RootInducedPayloadBytes);
        Assert.Equal(0, coarse.RootInducedVersionCount);
    }

    private static ResearchCommittedVersionSnapshot Version(string id, ulong sequence)
        => new(
            id,
            Guid.Parse($"30000000-0000-0000-0000-{sequence:D12}"),
            sequence,
            "key-a",
            KeyBytes: 4,
            ValueBytes: 10,
            IsTombstone: false);
}
