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
    public void RandomizedForestsMatchIndependentSlowObserverResolver()
    {
        const int scenarioCount = 500;
        for (var seed = 0; seed < scenarioCount; seed++)
        {
            var snapshot = RandomSnapshot(seed);
            var result = new ObserverExactErasureOracle(snapshot).Analyze("K");
            var expected = EnumerateReferenceObservers(snapshot, "K")
                .OrderBy(item => item.ObserverId, StringComparer.Ordinal)
                .ToArray();
            var actual = result.Observers
                .OrderBy(item => item.ObserverId, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected.Length, actual.Length);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index].ObserverId, actual[index].ObserverId);
                Assert.Equal(expected[index].Kind, actual[index].Kind);
                Assert.Equal(expected[index].HistoryId, actual[index].HistoryId);
                Assert.Equal(expected[index].Boundary, actual[index].Boundary);
                Assert.Equal(expected[index].Content, actual[index].Content);
                Assert.Equal(expected[index].ResolvedVersionId, actual[index].ResolvedVersionId);
                Assert.Equal(expected[index].ResolvedHistoryId, actual[index].ResolvedHistoryId);
                Assert.Equal(expected[index].ResolvedSequence, actual[index].ResolvedSequence);
                Assert.Equal(expected[index].ParentFallbackHops, actual[index].ParentFallbackHops);
            }

            Assert.Equal(
                expected.Where(item => item.Content == ErasureContentState.Value).Select(item => item.ObserverId).Order(StringComparer.Ordinal),
                result.BlockingObservers.Select(item => item.ObserverId).Order(StringComparer.Ordinal));
        }
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

    private static ResearchRetentionSnapshot RandomSnapshot(int seed)
    {
        var random = new Random(seed);
        var historyCount = random.Next(1, 7);
        var historyIds = Enumerable.Range(0, historyCount)
            .Select(index => DeterministicGuid(seed, index + 1))
            .ToArray();
        const ulong current = 4;
        var histories = new List<ResearchHistoryRetentionSnapshot>(historyCount);
        for (var historyIndex = 0; historyIndex < historyCount; historyIndex++)
        {
            var versions = new List<ResearchCommittedVersionSnapshot>();
            foreach (var key in new[] { "K", "X" })
            {
                for (ulong sequence = 1; sequence <= current; sequence++)
                {
                    if (random.NextDouble() >= 0.45)
                    {
                        continue;
                    }
                    var tombstone = random.NextDouble() < 0.25;
                    versions.Add(new ResearchCommittedVersionSnapshot(
                        $"{historyIds[historyIndex]:N}:{key}:{sequence}",
                        DeterministicGuid(seed, checked(100 + historyIndex * 10 + (int)sequence)),
                        sequence,
                        key,
                        1,
                        tombstone ? 0 : random.Next(1, 128),
                        tombstone));
                }
            }
            histories.Add(new ResearchHistoryRetentionSnapshot(
                historyIds[historyIndex],
                (ulong)random.Next(0, (int)current + 1),
                current,
                versions));
        }

        var roots = new List<ResearchPersistentRetentionRootSnapshot>();
        for (var historyIndex = 1; historyIndex < historyCount; historyIndex++)
        {
            var parentIndex = random.Next(0, historyIndex);
            roots.Add(new ResearchPersistentRetentionRootSnapshot(
                DeterministicGuid(seed, 1_000 + historyIndex),
                "BranchBase",
                historyIds[historyIndex],
                historyIds[parentIndex],
                (ulong)random.Next(0, (int)current + 1)));
        }
        for (var historyIndex = 0; historyIndex < historyCount; historyIndex++)
        {
            if (random.NextDouble() < 0.35)
            {
                roots.Add(new ResearchPersistentRetentionRootSnapshot(
                    DeterministicGuid(seed, 2_000 + historyIndex),
                    "PersistentSnapshot",
                    historyIds[historyIndex],
                    historyIds[historyIndex],
                    (ulong)random.Next(0, (int)current + 1)));
            }
        }

        var active = historyIds
            .Select((historyId, index) => (historyId, index))
            .Where(_ => random.NextDouble() < 0.30)
            .Select(item => new ResearchActiveRetentionBoundarySnapshot(
                item.historyId,
                (ulong)random.Next(0, (int)current + 1)))
            .ToArray();
        return new ResearchRetentionSnapshot(histories, roots, active);
    }

    private static ObserverExactErasureWitness[] EnumerateReferenceObservers(
        ResearchRetentionSnapshot snapshot,
        string keyId)
    {
        var observers = new Dictionary<string, (ErasureObserverContractKind Kind, Guid HistoryId, ulong Boundary)>(StringComparer.Ordinal);
        foreach (var history in snapshot.Histories)
        {
            AddReferenceGeneric(observers, history, history.RetentionFloor, keyId);
            foreach (var version in history.Versions.Where(item =>
                         item.KeyId == keyId && item.CommitSequence >= history.RetentionFloor))
            {
                AddReferenceGeneric(observers, history, version.CommitSequence, keyId);
            }
            AddReferenceGeneric(observers, history, history.CurrentSequence, keyId);
            observers[$"current:{history.HistoryId:N}:{keyId}"] =
                (ErasureObserverContractKind.CurrentState, history.HistoryId, history.CurrentSequence);
        }
        foreach (var active in snapshot.ActiveBoundaries)
        {
            observers[$"active:{active.ProtectedHistoryId:N}:{active.Boundary}"] =
                (ErasureObserverContractKind.ActiveBoundary, active.ProtectedHistoryId, active.Boundary);
        }
        foreach (var root in snapshot.PersistentRoots.Where(item => item.Kind != "BranchBase"))
        {
            observers[RootObserverId(root.RootId)] =
                (ErasureObserverContractKind.PersistentSnapshot, root.ProtectedHistoryId, root.Boundary);
        }

        var edges = snapshot.PersistentRoots
            .Where(item => item.Kind == "BranchBase")
            .ToDictionary(item => item.OwnerHistoryId, item => (item.ProtectedHistoryId, item.Boundary));
        return observers.Select(pair => ResolveReference(pair.Key, pair.Value, keyId, snapshot, edges)).ToArray();
    }

    private static void AddReferenceGeneric(
        Dictionary<string, (ErasureObserverContractKind Kind, Guid HistoryId, ulong Boundary)> target,
        ResearchHistoryRetentionSnapshot history,
        ulong boundary,
        string keyId)
    {
        var id = $"generic:{history.HistoryId:N}:{boundary}:{keyId}";
        target[id] = (ErasureObserverContractKind.GenericTimeTravel, history.HistoryId, boundary);
    }

    private static ObserverExactErasureWitness ResolveReference(
        string observerId,
        (ErasureObserverContractKind Kind, Guid HistoryId, ulong Boundary) observer,
        string keyId,
        ResearchRetentionSnapshot snapshot,
        Dictionary<Guid, (Guid ProtectedHistoryId, ulong Boundary)> edges)
    {
        var historyId = observer.HistoryId;
        var boundary = observer.Boundary;
        for (var hops = 0; hops <= snapshot.Histories.Count; hops++)
        {
            var history = snapshot.GetHistory(historyId);
            var local = history.Versions
                .Where(item => item.KeyId == keyId && item.CommitSequence <= boundary)
                .OrderBy(item => item.CommitSequence)
                .LastOrDefault();
            if (local is not null)
            {
                return new ObserverExactErasureWitness(
                    observerId,
                    observer.Kind,
                    observer.HistoryId,
                    observer.Boundary,
                    local.IsTombstone ? ErasureContentState.Tombstone : ErasureContentState.Value,
                    local.VersionId,
                    historyId,
                    local.CommitSequence,
                    hops);
            }
            if (!edges.TryGetValue(historyId, out var edge))
            {
                return new ObserverExactErasureWitness(
                    observerId,
                    observer.Kind,
                    observer.HistoryId,
                    observer.Boundary,
                    ErasureContentState.Absent,
                    null,
                    null,
                    null,
                    hops);
            }
            historyId = edge.ProtectedHistoryId;
            boundary = edge.Boundary;
        }
        throw new InvalidOperationException("Reference resolver exceeded history count.");
    }

    private static Guid DeterministicGuid(int seed, int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, seed);
        BitConverter.TryWriteBytes(bytes[4..], ordinal);
        BitConverter.TryWriteBytes(bytes[8..], unchecked(seed * 1_000_003 + ordinal));
        BitConverter.TryWriteBytes(bytes[12..], unchecked(seed * 97 + ordinal * 17));
        return new Guid(bytes);
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
