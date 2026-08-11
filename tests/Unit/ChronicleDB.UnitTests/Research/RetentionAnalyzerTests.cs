using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class RetentionAnalyzerTests
{
    [Fact]
    public void MarginalDebtSeparatesUniqueAndSharedVersions()
    {
        var shared = new RetentionVersion("v-shared", logicalPayloadBytes: 10, serializedBytes: 20);
        var onlyA = new RetentionVersion("v-a", logicalPayloadBytes: 30, serializedBytes: 40);
        var onlyB = new RetentionVersion("v-b", logicalPayloadBytes: 50, serializedBytes: 60);
        var context = new RetentionContext(
            globallyRequiredVersions: [],
            roots:
            [
                new RetentionRoot("A", [shared, onlyA]),
                new RetentionRoot("B", [shared, onlyB]),
            ]);

        var result = MarginalRetentionAnalyzer.Analyze(context, ["A"]);

        Assert.Equal(3, result.ProtectedVersionCount);
        Assert.Equal(2, result.ProtectedVersionCountAfterDrop);
        Assert.Equal(90, result.CurrentLivePayloadBytes);
        Assert.Equal(60, result.LivePayloadBytesAfterDrop);
        Assert.Equal(30, result.MarginalPayloadBytes);
        Assert.Equal(1, result.UniqueRequiredVersionCount);
        Assert.Equal(1, result.SharedRequiredVersionCount);
        Assert.Equal(30, result.UniqueProtectedPayloadBytes);
        Assert.Equal(10, result.SharedProtectedPayloadBytes);
    }

    [Fact]
    public void GlobalRequirementCannotBeDroppedByRootSet()
    {
        var global = new RetentionVersion("floor", logicalPayloadBytes: 7, serializedBytes: 9);
        var rootVersion = new RetentionVersion("root", logicalPayloadBytes: 11, serializedBytes: 13);
        var context = new RetentionContext(
            globallyRequiredVersions: [global],
            roots: [new RetentionRoot("A", [rootVersion])]);

        var result = MarginalRetentionAnalyzer.Analyze(context, ["A"]);

        Assert.Equal(18, result.CurrentLivePayloadBytes);
        Assert.Equal(7, result.LivePayloadBytesAfterDrop);
        Assert.Equal(11, result.MarginalPayloadBytes);
    }

    [Fact]
    public void ConflictingVersionMetadataIsRejected()
    {
        var context = new RetentionContext(
            globallyRequiredVersions: [new RetentionVersion("v", 10, 20)],
            roots: [new RetentionRoot("A", [new RetentionVersion("v", 11, 20)])]);

        Assert.Throws<ArgumentException>(() => MarginalRetentionAnalyzer.Analyze(context, ["A"]));
    }

    [Fact]
    public void UnknownOrEmptyRootSetIsRejected()
    {
        var context = new RetentionContext(
            globallyRequiredVersions: [],
            roots: [new RetentionRoot("A", [])]);

        Assert.Throws<ArgumentException>(() => MarginalRetentionAnalyzer.Analyze(context, []));
        Assert.Throws<ArgumentException>(() => MarginalRetentionAnalyzer.Analyze(context, ["missing"]));
    }

    [Fact]
    public void TombstonesCanCarrySerializedCostWithoutLogicalPayload()
    {
        var tombstone = new RetentionVersion("tombstone", logicalPayloadBytes: 0, serializedBytes: 8, isTombstone: true);
        var context = new RetentionContext([], [new RetentionRoot("A", [tombstone])]);

        var result = MarginalRetentionAnalyzer.Analyze(context, ["A"]);

        Assert.Equal(0, result.MarginalPayloadBytes);
        Assert.Equal(8, result.MarginalSerializedBytes);
    }
    [Fact]
    public void ObserverExactInspectorComputesNonAdditiveRootDebt()
    {
        var historyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var rootA = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var rootB = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var versions = new[]
        {
            new ResearchCommittedVersionSnapshot("v1", Guid.NewGuid(), 1, "key", 1, 100, false),
            new ResearchCommittedVersionSnapshot("v2", Guid.NewGuid(), 10, "key", 1, 100, false),
        };
        var snapshot = new ResearchRetentionSnapshot(
            [new ResearchHistoryRetentionSnapshot(historyId, 10, 10, versions)],
            [
                new ResearchPersistentRetentionRootSnapshot(rootA, "Snapshot", historyId, historyId, 1),
                new ResearchPersistentRetentionRootSnapshot(rootB, "Snapshot", historyId, historyId, 1),
            ],
            []);

        var inspector = new RetentionInspector(snapshot);

        var dropA = inspector.WhatIfDrop(rootA);
        var dropBoth = inspector.WhatIfDrop([rootA, rootB]);

        Assert.Equal(0, dropA.MarginalPayloadBytes);
        Assert.Equal(100, dropBoth.MarginalPayloadBytes);
        Assert.Equal(2, dropBoth.ProtectedVersionCount);
        Assert.Equal(1, dropBoth.ProtectedVersionCountAfterDrop);
    }


    [Fact]
    public void MarginalDebtMatchesIndependentBruteForceOracleAcrossRandomRootSets()
    {
        for (var seed = 1; seed <= 250; seed++)
        {
            var random = new Random(seed);
            var versionCount = random.Next(4, 18);
            var rootCount = random.Next(2, 7);
            var versions = Enumerable.Range(0, versionCount)
                .Select(index => new RetentionVersion(
                    $"v{index}",
                    logicalPayloadBytes: random.Next(0, 4097),
                    serializedBytes: random.Next(4097, 8193)))
                .ToArray();

            // Rebuild serialized sizes so every version satisfies serialized >= logical.
            versions = versions
                .Select((version, index) => new RetentionVersion(
                    version.VersionId,
                    version.LogicalPayloadBytes,
                    version.LogicalPayloadBytes + 8 + index,
                    version.LogicalPayloadBytes == 0))
                .ToArray();

            var global = versions
                .Where(_ => random.NextDouble() < 0.15)
                .ToArray();
            var roots = Enumerable.Range(0, rootCount)
                .Select(rootIndex => new RetentionRoot(
                    $"r{rootIndex}",
                    versions.Where(_ => random.NextDouble() < 0.45).ToArray()))
                .ToArray();
            var context = new RetentionContext(global, roots);

            var selected = roots
                .Where(_ => random.NextDouble() < 0.5)
                .Select(root => root.RootId)
                .ToArray();
            if (selected.Length == 0)
            {
                selected = [roots[0].RootId];
            }

            var actual = MarginalRetentionAnalyzer.Analyze(context, selected);
            var expected = BruteForceMarginalDebt(context, selected);

            Assert.Equal(expected.CurrentCount, actual.ProtectedVersionCount);
            Assert.Equal(expected.AfterCount, actual.ProtectedVersionCountAfterDrop);
            Assert.Equal(expected.CurrentPayloadBytes, actual.CurrentLivePayloadBytes);
            Assert.Equal(expected.AfterPayloadBytes, actual.LivePayloadBytesAfterDrop);
            Assert.Equal(expected.MarginalPayloadBytes, actual.MarginalPayloadBytes);
            Assert.Equal(expected.CurrentSerializedBytes, actual.CurrentSerializedBytes);
            Assert.Equal(expected.AfterSerializedBytes, actual.SerializedBytesAfterDrop);
            Assert.Equal(expected.MarginalSerializedBytes, actual.MarginalSerializedBytes);
        }
    }

    private static BruteForceRetentionResult BruteForceMarginalDebt(
        RetentionContext context,
        IReadOnlyCollection<string> selectedRootIds)
    {
        static Dictionary<string, RetentionVersion> Union(IEnumerable<RetentionVersion> values)
        {
            var result = new Dictionary<string, RetentionVersion>(StringComparer.Ordinal);
            foreach (var version in values)
            {
                result[version.VersionId] = version;
            }

            return result;
        }

        var all = Union(context.GloballyRequiredVersions.Concat(context.Roots.SelectMany(root => root.RequiredVersions)));
        var remaining = Union(context.GloballyRequiredVersions.Concat(
            context.Roots
                .Where(root => !selectedRootIds.Contains(root.RootId, StringComparer.Ordinal))
                .SelectMany(root => root.RequiredVersions)));

        static long SumPayload(IEnumerable<RetentionVersion> values)
            => values.Sum(version => version.LogicalPayloadBytes);
        static long SumSerialized(IEnumerable<RetentionVersion> values)
            => values.Sum(version => version.SerializedBytes);

        var currentPayload = SumPayload(all.Values);
        var afterPayload = SumPayload(remaining.Values);
        var currentSerialized = SumSerialized(all.Values);
        var afterSerialized = SumSerialized(remaining.Values);
        return new BruteForceRetentionResult(
            all.Count,
            remaining.Count,
            currentPayload,
            afterPayload,
            currentPayload - afterPayload,
            currentSerialized,
            afterSerialized,
            currentSerialized - afterSerialized);
    }

    private sealed record BruteForceRetentionResult(
        int CurrentCount,
        int AfterCount,
        long CurrentPayloadBytes,
        long AfterPayloadBytes,
        long MarginalPayloadBytes,
        long CurrentSerializedBytes,
        long AfterSerializedBytes,
        long MarginalSerializedBytes);

    [Fact]
    public void ActiveBoundaryPreventsFalseCounterfactualReclaim()
    {
        var historyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var rootId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var versions = new[]
        {
            new ResearchCommittedVersionSnapshot("v1", Guid.NewGuid(), 1, "key", 1, 100, false),
            new ResearchCommittedVersionSnapshot("v2", Guid.NewGuid(), 10, "key", 1, 100, false),
        };
        var snapshot = new ResearchRetentionSnapshot(
            [new ResearchHistoryRetentionSnapshot(historyId, 10, 10, versions)],
            [new ResearchPersistentRetentionRootSnapshot(rootId, "Snapshot", historyId, historyId, 1)],
            [new ResearchActiveRetentionBoundarySnapshot(historyId, 1)]);

        var inspector = new RetentionInspector(snapshot);

        var result = inspector.WhatIfDrop(rootId);

        Assert.Equal(0, result.MarginalPayloadBytes);
        Assert.Equal(2, result.ProtectedVersionCountAfterDrop);
    }

}
