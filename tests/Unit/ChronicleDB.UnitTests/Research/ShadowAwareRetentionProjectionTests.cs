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

    [Fact]
    public void RandomizedSmallHistoryForestsPreserveAndMinimizeObserverState()
    {
        const int scenarioCount = 400;
        for (var seed = 0; seed < scenarioCount; seed++)
        {
            var snapshot = RandomSnapshot(seed);
            var result = new ShadowAwareRetentionProjection(snapshot).Analyze();

            Assert.True(result.CandidateIsSubsetOfBaseline);
            Assert.True(result.ObserverEquivalenceVerified);
            Assert.Empty(result.ObserverMismatches);
            Assert.True(result.ObserverMinimalityVerified);
            Assert.Empty(result.UnwitnessedRequiredVersionIds);
            Assert.True(result.ShadowReleasedPayloadBytes >= 0);
        }
    }

    [Fact]
    public void IndependentFlatExactBaselineMatchesRetentionInspectorAcrossRandomForests()
    {
        const int scenarioCount = 1_000;
        for (var seed = 0; seed < scenarioCount; seed++)
        {
            var snapshot = RandomSnapshot(seed + 10_000);
            var independent = new FlatExactRetentionProjectionBaseline(snapshot).Analyze();
            var inspector = new RetentionInspector(snapshot).Context;
            var inspectorIds = inspector.GloballyRequiredVersions
                .Concat(inspector.Roots.SelectMany(root => root.RequiredVersions))
                .Select(version => version.VersionId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(inspectorIds, independent.RequiredVersionIds);

            var candidate = new ShadowAwareRetentionProjection(snapshot).Analyze();
            Assert.True(candidate.FlatExactBaselineVerified);
            Assert.Empty(candidate.FlatExactBaselineMismatchVersionIds);
        }
    }

    private static ResearchRetentionSnapshot RandomSnapshot(int seed)
    {
        var random = new Random(seed);
        var historyCount = random.Next(2, 7);
        var historyIds = Enumerable.Range(0, historyCount).Select(_ => Guid.NewGuid()).ToArray();
        const ulong current = 4;
        var histories = new List<ResearchHistoryRetentionSnapshot>(historyCount);
        var keys = new[] { "K0", "K1", "K2", "K3" };

        for (var historyIndex = 0; historyIndex < historyCount; historyIndex++)
        {
            var versions = new List<ResearchCommittedVersionSnapshot>();
            foreach (var key in keys)
            {
                var sequences = Enumerable.Range(1, (int)current)
                    .Where(_ => random.NextDouble() < 0.45)
                    .Select(value => (ulong)value)
                    .ToArray();
                foreach (var sequence in sequences)
                {
                    var tombstone = random.NextDouble() < 0.20;
                    versions.Add(Version(
                        historyIds[historyIndex],
                        key,
                        sequence,
                        tombstone ? 0 : random.Next(1, 257),
                        tombstone));
                }
            }

            histories.Add(History(
                historyIds[historyIndex],
                floor: (ulong)random.Next(0, (int)current + 1),
                current: current,
                versions.ToArray()));
        }

        var roots = new List<ResearchPersistentRetentionRootSnapshot>();
        for (var historyIndex = 1; historyIndex < historyCount; historyIndex++)
        {
            var parentIndex = random.Next(0, historyIndex);
            roots.Add(BranchBase(
                historyIds[historyIndex],
                historyIds[parentIndex],
                boundary: (ulong)random.Next(0, (int)current + 1)));
        }

        foreach (var historyId in historyIds)
        {
            if (random.NextDouble() < 0.35)
            {
                roots.Add(PersistentSnapshot(historyId, (ulong)random.Next(0, (int)current + 1)));
            }
        }

        var active = historyIds
            .Where(_ => random.NextDouble() < 0.30)
            .Select(historyId => new ResearchActiveRetentionBoundarySnapshot(
                historyId,
                (ulong)random.Next(0, (int)current + 1)))
            .ToArray();

        return Snapshot(histories, roots, active);
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
