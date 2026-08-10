using ChronicleDB.Core.Keys;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.Files;

namespace ChronicleDB.PersistenceTests;

public sealed class SnapshotIntegrationTests
{
    [Fact]
    public void PersistentSnapshotRemainsStableAcrossLaterWritesAndRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid snapshotId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            database.Put([2], [20]);
            using var snapshot = database.CreateSnapshot("baseline");
            snapshotId = snapshot.SnapshotId;
            Assert.Equal((ulong)2, snapshot.Sequence);

            database.Put([1], [11]);
            database.Delete([2]);
            database.Put([3], [30]);

            Assert.True(snapshot.TryGet([1], out var oldOne));
            Assert.Equal(new byte[] { 10 }, oldOne);
            Assert.True(snapshot.TryGet([2], out var oldTwo));
            Assert.Equal(new byte[] { 20 }, oldTwo);
            Assert.False(snapshot.TryGet([3], out _));
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var byId = reopened.OpenSnapshot(snapshotId);
        using var byName = reopened.OpenSnapshot("baseline");
        Assert.Equal(byId.Info, byName.Info);
        Assert.True(byId.TryGet([1], out var value));
        Assert.Equal(new byte[] { 10 }, value);
        Assert.True(byId.TryGet([2], out var deletedLater));
        Assert.Equal(new byte[] { 20 }, deletedLater);
        Assert.False(byId.TryGet([3], out _));
    }

    [Fact]
    public void HistoricalViewUsesFixedCommitBoundary()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        var firstSequence = database.CurrentCommitSequence.Value;
        database.Put([1], [2]);
        database.Delete([1]);

        using var first = database.OpenHistoricalView(firstSequence);
        using var second = database.OpenHistoricalView(2);
        using var deleted = database.OpenHistoricalView(3);

        Assert.True(first.TryGet([1], out var firstValue));
        Assert.Equal(new byte[] { 1 }, firstValue);
        Assert.True(second.TryGet([1], out var secondValue));
        Assert.Equal(new byte[] { 2 }, secondValue);
        Assert.False(deleted.TryGet([1], out _));
    }

    [Fact]
    public void DeletingNamedSnapshotDoesNotInvalidateAlreadyOpenHandle()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [7]);
        using var snapshot = database.CreateSnapshot("temporary");

        database.DeleteSnapshot(snapshot.SnapshotId);

        Assert.Empty(database.ListSnapshots());
        Assert.Throws<ChronicleDB.SnapshotNotFoundException>(() => database.OpenSnapshot(snapshot.SnapshotId));
        Assert.True(snapshot.TryGet([1], out var retained));
        Assert.Equal(new byte[] { 7 }, retained);
    }


    [Fact]
    public void DeletedSnapshotStaysDeletedAfterRestartAndNameMayBeReused()
    {
        using var directory = new StorageTestDirectory();
        Guid deletedId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [7]);
            using var snapshot = database.CreateSnapshot("reusable");
            deletedId = snapshot.SnapshotId;
            database.DeleteSnapshot(deletedId);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.Empty(reopened.ListSnapshots());
        Assert.Throws<ChronicleDB.SnapshotNotFoundException>(() => reopened.OpenSnapshot(deletedId));
        using var replacement = reopened.CreateSnapshot("reusable");
        Assert.NotEqual(deletedId, replacement.SnapshotId);
    }

    [Fact]
    public async Task ConcurrentCreationOfSameSnapshotNameHasExactlyOneDurableWinner()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                using var snapshot = database.CreateSnapshot("one-name");
                return true;
            }
            catch (ChronicleDB.SnapshotNameConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, results.Count(result => result));
        Assert.Single(database.ListSnapshots());
        Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
    }

    [Fact]
    public void DuplicateSnapshotNameAndUnknownSnapshotUseExplicitErrors()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var snapshot = database.CreateSnapshot("stable");

        Assert.Throws<ChronicleDB.SnapshotNameConflictException>(() => database.CreateSnapshot("stable"));
        Assert.Throws<ChronicleDB.SnapshotNotFoundException>(() => database.OpenSnapshot(Guid.NewGuid()));
        Assert.Throws<ChronicleDB.SnapshotNotFoundException>(() => database.DeleteSnapshot(Guid.NewGuid()));
    }

    [Fact]
    public void HistoricalSequenceOutsideRetainedRangeIsRejected()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);

        Assert.Throws<ChronicleDB.HistoricalStateUnavailableException>(
            () => database.OpenHistoricalView(database.CurrentCommitSequence.Value + 1));
    }

    [Fact]
    public void FirstV05OpenOfExistingDatabaseEstablishesConservativeRetentionFloor()
    {
        using var directory = new StorageTestDirectory();
        using (var preHistoryStore = PersistentKeyValueStore.Open(directory.Path))
        {
            preHistoryStore.Put(new BinaryKey([1]), [10]);
            preHistoryStore.Put(new BinaryKey([1]), [20]);
        }

        // The pre-history physical state has no commit-sequence provenance. v0.5 must
        // bootstrap it at the upgrade boundary instead of inventing older history.
        using var upgraded = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.Equal(upgraded.CurrentCommitSequence.Value, upgraded.HistoricalRetentionFloor);
        Assert.Throws<ChronicleDB.HistoricalStateUnavailableException>(() => upgraded.OpenHistoricalView(0));
        using var current = upgraded.OpenHistoricalView(upgraded.CurrentCommitSequence.Value);
        Assert.True(current.TryGet([1], out var value));
        Assert.Equal(new byte[] { 20 }, value);
    }


    [Fact]
    public void LegacyPhysicalStateGetsDurableBootstrapSequenceAndRemainsVisibleToSnapshotsAfterRestart()
    {
        using var directory = new StorageTestDirectory();
        using (var legacy = PersistentKeyValueStore.Open(directory.Path))
        {
            legacy.Put(new BinaryKey([1]), [10]);
        }

        Guid snapshotId;
        ulong bootstrapSequence;
        using (var upgraded = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            bootstrapSequence = upgraded.CurrentCommitSequence.Value;
            Assert.NotEqual((ulong)0, bootstrapSequence);
            Assert.Equal(bootstrapSequence, upgraded.HistoricalRetentionFloor);
            using var snapshot = upgraded.CreateSnapshot("legacy-boundary");
            snapshotId = snapshot.SnapshotId;
            Assert.Equal(bootstrapSequence, snapshot.Sequence);

            upgraded.Put([2], [20]);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.Equal(bootstrapSequence + 1, reopened.CurrentCommitSequence.Value);
        using var historical = reopened.OpenSnapshot(snapshotId);
        Assert.True(historical.TryGet([1], out var legacyValue));
        Assert.Equal(new byte[] { 10 }, legacyValue);
        Assert.False(historical.TryGet([2], out _));
    }

    [Fact]
    public void SnapshotDiagnosticsExposeRetentionAndVersionDepth()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        database.Put([1], [2]);
        using var snapshot = database.CreateSnapshot("metrics");

        var diagnostics = database.GetDiagnostics();

        Assert.Equal(1, diagnostics.SnapshotCount);
        Assert.Equal(1, diagnostics.RetainingRootCount);
        Assert.Equal((ulong?)2, diagnostics.OldestSnapshotSequence);
        Assert.Equal(2, diagnostics.VersionCount);
        Assert.Equal(2, diagnostics.MaximumVersionChainLength);
        Assert.True(diagnostics.WalFlushCount >= 2);
        Assert.True(diagnostics.DataPageCount >= 2);
    }

    [Fact]
    public void SnapshotDeletionReleasesItsHistoryRootWithoutChangingTheConservativeFloor()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        using var snapshot = database.CreateSnapshot("release-root");

        var beforeDelete = database.GetDiagnostics();
        Assert.Equal(1, beforeDelete.RetainingRootCount);
        database.DeleteSnapshot(snapshot.SnapshotId);

        var diagnostics = database.GetDiagnostics();
        Assert.Equal(0, diagnostics.RetainingRootCount);
        Assert.Equal(beforeDelete.RetentionFloor, diagnostics.RetentionFloor);
    }
    [Fact]
    public void SnapshotFailureBeforeMetadataWriteDoesNotFaultDatabase()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowingSnapshotFaultInjector(StorageFaultPoint.BeforeSnapshotRecordWrite);
        using var database = ChronicleDB.ChronicleDatabase.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });

        Assert.Throws<InvalidOperationException>(() => database.CreateSnapshot("not-written"));
        Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
        Assert.Empty(database.ListSnapshots());

        database.Put([1], [9]);
        Assert.True(database.TryGet([1], out var value));
        Assert.Equal(new byte[] { 9 }, value);
    }

    [Fact]
    public void SnapshotFailureAfterDurabilityFaultsDatabaseAndRecoveryRestoresSnapshot()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowingSnapshotFaultInjector(StorageFaultPoint.AfterSnapshotFlush);
        using (var database = ChronicleDB.ChronicleDatabase.Open(
                   directory.Path,
                   new StorageOptions { FaultInjector = injector }))
        {
            database.Put([1], [7]);
            Assert.Throws<InvalidOperationException>(() => database.CreateSnapshot("durable-but-unacknowledged"));
            Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            Assert.Throws<ChronicleDB.ChronicleDatabaseFaultedException>(() => database.TryGet([1], out _));
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var snapshot = reopened.OpenSnapshot("durable-but-unacknowledged");
        Assert.True(snapshot.TryGet([1], out var value));
        Assert.Equal(new byte[] { 7 }, value);
    }

    [Fact]
    public void HistoryRootFailureAfterDurabilityFaultsDatabaseAndRecoveryKeepsSnapshot()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowingSnapshotFaultInjector(StorageFaultPoint.AfterHistoryRootFlush);
        using (var database = ChronicleDB.ChronicleDatabase.Open(
                   directory.Path,
                   new StorageOptions { FaultInjector = injector }))
        {
            database.Put([1], [8]);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var snapshot = database.CreateSnapshot("root-durable-but-unacknowledged");
            });
            Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var snapshotAfterRecovery = reopened.OpenSnapshot("root-durable-but-unacknowledged");
        Assert.True(snapshotAfterRecovery.TryGet([1], out var value));
        Assert.Equal(new byte[] { 8 }, value);
    }

    private sealed class ThrowingSnapshotFaultInjector(StorageFaultPoint target) : IStorageFaultInjector
    {
        public void Hit(StorageFaultPoint point, ChronicleDB.Core.Identifiers.PageId pageId)
        {
            if (point == target)
            {
                throw new InvalidOperationException($"Injected snapshot fault at {point}.");
            }
        }
    }

}
