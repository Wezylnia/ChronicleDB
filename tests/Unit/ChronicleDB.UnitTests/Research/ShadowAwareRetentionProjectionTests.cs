using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ShadowAwareRetentionProjectionTests
{
    [Fact]
    public void ChildOverwriteCanReleaseParentBaseVersion()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, floor: 2, current: 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(child, floor: 1, current: 1,
                    Version(child, "K", 1, 100)),
            ],
            [BranchBase(child, main, 1)]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.True(result.CandidateIsSubsetOfBaseline);
        Assert.Equal(3, result.BaselineVersionCount);
        Assert.Equal(2, result.ShadowAwareVersionCount);
        Assert.Equal(100, result.ShadowReleasedPayloadBytes);
        Assert.Contains(VersionId(main, "K", 1), result.ReleasedVersionIds);
        Assert.DoesNotContain(VersionId(main, "K", 1), result.RequiredVersionIds);
    }

    [Fact]
    public void PreShadowSnapshotStillRequiresParentBaseVersion()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(child, 1, 1, Version(child, "K", 1, 100)),
            ],
            [
                BranchBase(child, main, 1),
                PersistentSnapshot(child, boundary: 0),
            ]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Equal(0, result.ShadowReleasedPayloadBytes);
        Assert.Contains(VersionId(main, "K", 1), result.RequiredVersionIds);
        Assert.True(result.ParentFallbackHops > 0);
    }

    [Fact]
    public void PostShadowSnapshotDoesNotRequireParentBaseVersion()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(child, 1, 1, Version(child, "K", 1, 100)),
            ],
            [
                BranchBase(child, main, 1),
                PersistentSnapshot(child, boundary: 1),
            ]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Equal(100, result.ShadowReleasedPayloadBytes);
        Assert.DoesNotContain(VersionId(main, "K", 1), result.RequiredVersionIds);
    }

    [Fact]
    public void TombstoneStopsParentFallback()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(child, 1, 1, Version(child, "K", 1, 0, tombstone: true)),
            ],
            [BranchBase(child, main, 1)]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Equal(100, result.ShadowReleasedPayloadBytes);
        Assert.DoesNotContain(VersionId(main, "K", 1), result.RequiredVersionIds);
        Assert.Contains(VersionId(child, "K", 1), result.RequiredVersionIds);
    }

    [Fact]
    public void NestedFallbackStopsAtNearestShadowingAncestor()
    {
        var main = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(a, 1, 1, Version(a, "K", 1, 100)),
                History(b, 0, 0),
            ],
            [
                BranchBase(a, main, 1),
                BranchBase(b, a, 1),
            ]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Equal(100, result.ShadowReleasedPayloadBytes);
        Assert.DoesNotContain(VersionId(main, "K", 1), result.RequiredVersionIds);
        Assert.Contains(VersionId(a, "K", 1), result.RequiredVersionIds);
        Assert.True(result.ParentFallbackHops > 0);
    }

    [Fact]
    public void OneUnshadowedSiblingKeepsSharedParentVersion()
    {
        var main = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(a, 1, 1, Version(a, "K", 1, 100)),
                History(b, 0, 0),
            ],
            [
                BranchBase(a, main, 1),
                BranchBase(b, main, 1),
            ]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Equal(0, result.ShadowReleasedPayloadBytes);
        Assert.Contains(VersionId(main, "K", 1), result.RequiredVersionIds);
    }

    [Fact]
    public void AllShadowingSiblingsCanReleaseSharedParentVersion()
    {
        var main = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "K", 2, 100)),
                History(a, 1, 1, Version(a, "K", 1, 100)),
                History(b, 1, 1, Version(b, "K", 1, 100)),
            ],
            [
                BranchBase(a, main, 1),
                BranchBase(b, main, 1),
            ]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Equal(100, result.ShadowReleasedPayloadBytes);
        Assert.DoesNotContain(VersionId(main, "K", 1), result.RequiredVersionIds);
    }

    [Fact]
    public void UnshadowedKeyStillFallsBackWhileShadowedKeyIsReleased()
    {
        var main = Guid.NewGuid();
        var child = Guid.NewGuid();
        var snapshot = Snapshot(
            [
                History(main, 2, 2,
                    Version(main, "K", 1, 100),
                    Version(main, "X", 1, 100),
                    Version(main, "K", 2, 100),
                    Version(main, "X", 2, 100)),
                History(child, 1, 1, Version(child, "K", 1, 100)),
            ],
            [BranchBase(child, main, 1)]);

        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

        Assert.True(result.ObserverEquivalenceVerified);
        Assert.Empty(result.ObserverMismatches);
        Assert.True(result.ObserverMinimalityVerified);
        Assert.Empty(result.UnwitnessedRequiredVersionIds);
        Assert.Contains(VersionId(main, "X", 1), result.RequiredVersionIds);
        Assert.DoesNotContain(VersionId(main, "K", 1), result.RequiredVersionIds);
        Assert.Equal(100, result.ShadowReleasedPayloadBytes);
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
        => new(Guid.NewGuid(), "BranchBase", child, parent, boundary);

    private static ResearchPersistentRetentionRootSnapshot PersistentSnapshot(Guid history, ulong boundary)
        => new(Guid.NewGuid(), "PersistentSnapshot", history, history, boundary);
}
