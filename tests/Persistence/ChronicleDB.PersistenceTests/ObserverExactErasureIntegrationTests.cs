using System.Security.Cryptography;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ObserverExactErasureIntegrationTests
{
    [Fact]
    public void NestedBranchSnapshotWitnessMatchesActualInheritedReadAndExposesLegacyP6Miss()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x41 };
        var value = new byte[] { 0x51, 0x52, 0x53 };
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put(key, value);
        using var parent = database.CreateBranch("a8-parent");
        using var nested = parent.CreateBranch("a8-nested");
        using var snapshot = nested.CreateSnapshot("a8-inherited-snapshot");

        Assert.True(snapshot.TryGet(key, out var actual));
        Assert.Equal(value, actual);

        var keyId = KeyId(key);
        var retention = database.CaptureResearchRetentionSnapshot();
        var oracle = new ObserverExactErasureOracle(retention).Analyze(keyId);
        var witness = Assert.Single(oracle.BlockingObservers, item =>
            item.ObserverId == RootObserverId(snapshot.Info.SnapshotId));
        Assert.Equal(ErasureContentState.Value, witness.Content);
        Assert.Equal(2, witness.ParentFallbackHops);
        Assert.NotEqual(nested.HistoryId, witness.ResolvedHistoryId);

        var legacyInput = database.CaptureResearchErasureClosureInput(key);
        var legacyRoot = Assert.Single(legacyInput.Representations, item =>
            item.RepresentationId == RootObserverId(snapshot.Info.SnapshotId));
        Assert.Equal(ErasureContentState.Absent, legacyRoot.Content);
        Assert.Contains(witness.ObserverId, oracle.BlockingObserverIdsUnrepresentedByLegacyP6);
    }

    [Fact]
    public void NestedBranchTombstoneWitnessMatchesActualMissingReadAndStopsMainFallback()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x42 };
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put(key, [0x61]);
        using var parent = database.CreateBranch("a8-delete-parent");
        Assert.True(parent.Delete(key));
        using var nested = parent.CreateBranch("a8-delete-nested");
        using var snapshot = nested.CreateSnapshot("a8-delete-snapshot");

        Assert.False(snapshot.TryGet(key, out _));

        var oracle = new ObserverExactErasureOracle(database.CaptureResearchRetentionSnapshot()).Analyze(KeyId(key));
        var witness = Assert.Single(oracle.Observers, item =>
            item.ObserverId == RootObserverId(snapshot.Info.SnapshotId));
        Assert.Equal(ErasureContentState.Tombstone, witness.Content);
        Assert.Equal(parent.HistoryId, witness.ResolvedHistoryId);
        Assert.Equal(1, witness.ParentFallbackHops);
        Assert.DoesNotContain(oracle.BlockingObservers, item => item.ObserverId == witness.ObserverId);
    }

    [Fact]
    public void GenericMainTimeTravelWitnessMatchesActualHistoricalReadWithoutSnapshotRoot()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x43 };
        var historicalValue = new byte[] { 0x71, 0x72 };
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put(key, historicalValue);
        var historicalSequence = database.CurrentCommitSequence.Value;
        Assert.True(database.Delete(key));

        using (var historical = database.OpenHistoricalView(historicalSequence))
        {
            Assert.True(historical.TryGet(key, out var actual));
            Assert.Equal(historicalValue, actual);
        }

        var retention = database.CaptureResearchRetentionSnapshot();
        Assert.Empty(retention.ActiveBoundaries);
        var oracle = new ObserverExactErasureOracle(retention).Analyze(KeyId(key));
        var witness = Assert.Single(oracle.BlockingObservers, item =>
            item.Kind == ErasureObserverContractKind.GenericTimeTravel
            && item.HistoryId == database.GetHistoryTopologyDiagnostics().Main.HistoryId
            && item.Boundary == historicalSequence);
        Assert.Equal(ErasureContentState.Value, witness.Content);
        Assert.DoesNotContain(oracle.LegacyLocalRootBlockers, item => !item.Kind.Equals("BranchBase", StringComparison.Ordinal));
        Assert.Contains(witness.ObserverId, oracle.BlockingObserverIdsUnrepresentedByLegacyP6);
    }


    [Fact]
    public void ActiveNestedHistoricalObserverIsCapturedByOracleButNotLegacyP6ObserverRoots()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x44 };
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put(key, [0x81]);
        using var parent = database.CreateBranch("a8-active-parent");
        using var nested = parent.CreateBranch("a8-active-nested");
        using var historical = nested.OpenHistoricalView(nested.CurrentSequence);

        Assert.True(historical.TryGet(key, out var actual));
        Assert.Equal([0x81], actual);

        var retention = database.CaptureResearchRetentionSnapshot();
        Assert.Contains(retention.ActiveBoundaries, item =>
            item.ProtectedHistoryId == nested.HistoryId && item.Boundary == historical.Sequence);
        var oracle = new ObserverExactErasureOracle(retention).Analyze(KeyId(key));
        var active = Assert.Single(oracle.BlockingObservers, item =>
            item.Kind == ErasureObserverContractKind.ActiveBoundary
            && item.HistoryId == nested.HistoryId
            && item.Boundary == historical.Sequence);
        Assert.Equal(2, active.ParentFallbackHops);

        var legacy = database.CaptureResearchErasureClosureInput(key);
        Assert.DoesNotContain(legacy.Representations, item =>
            item.Kind == ErasureRepresentationKind.ActiveTransactionRoot
            && item.OwnerHistoryId == nested.HistoryId);
        Assert.Contains(active.ObserverId, oracle.BlockingObserverIdsUnrepresentedByLegacyP6);
    }

    private static string KeyId(byte[] key)
        => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();

    private static string RootObserverId(Guid rootId) => $"root:{rootId:N}";
}
