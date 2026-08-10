using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.HistoryRoots;
using ChronicleDB.Storage.Snapshots;

namespace ChronicleDB.RecoveryTests;

public sealed class SnapshotRecoveryTests
{
    [Fact]
    public void SnapshotMetadataWithFutureBoundaryRejectsDatabaseOpen()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [1]);
            databaseId = database.DatabaseId;
        }

        using (var snapshots = PersistentSnapshotStore.Open(
                   directory.Path,
                   databaseId,
                   CommitSequence.Initial))
        {
            snapshots.AppendCreate(
                SnapshotId.New(),
                new CommitSequence(100),
                1,
                "future");
        }

        Assert.Throws<ChronicleDB.Storage.StorageCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path));
    }

    [Fact]
    public void DeletedSnapshotWithFutureBoundaryStillRejectsDatabaseOpen()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [1]);
            databaseId = database.DatabaseId;
        }

        var snapshotId = SnapshotId.New();
        using (var snapshots = PersistentSnapshotStore.Open(
                   directory.Path,
                   databaseId,
                   CommitSequence.Initial))
        {
            snapshots.AppendCreate(snapshotId, new CommitSequence(100), 1, "future-deleted");
            snapshots.AppendDelete(snapshotId);
        }

        Assert.Throws<ChronicleDB.Storage.StorageCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path));
    }

    [Fact]
    public void SnapshotCreatedBeforeLaterCrashRetainsHistoricalStateAfterRecovery()
    {
        using var directory = new StorageTestDirectory();
        Guid snapshotId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            using var snapshot = database.CreateSnapshot("before-later-write");
            snapshotId = snapshot.SnapshotId;
            database.Put([1], [20]);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        using var historical = reopened.OpenSnapshot(snapshotId);
        Assert.True(historical.TryGet([1], out var value));
        Assert.Equal(new byte[] { 10 }, value);
        Assert.True(reopened.TryGet([1], out var current));
        Assert.Equal(new byte[] { 20 }, current);
    }

    [Fact]
    public void MissingSnapshotRootRecordIsRebuiltBeforeDatabaseOpen()
    {
        using var directory = new StorageTestDirectory();
        Guid snapshotId;
        Guid databaseId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            databaseId = database.DatabaseId;
            using var snapshot = database.CreateSnapshot("root-repair");
            snapshotId = snapshot.SnapshotId;
        }

        var rootPath = Path.Combine(directory.Path, PersistentHistoryRootStore.FileName);
        using (var stream = new FileStream(rootPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(HistoryRootStoreHeaderCodec.Size);
            stream.Flush(flushToDisk: true);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        using var snapshotAfterRepair = reopened.OpenSnapshot(snapshotId);
        Assert.True(snapshotAfterRepair.TryGet([1], out var value));
        Assert.Equal(new byte[] { 10 }, value);
        Assert.True(new FileInfo(rootPath).Length > HistoryRootStoreHeaderCodec.Size);
    }

    [Fact]
    public void OrphanedSnapshotRootIsTombstonedDuringOpenReconciliation()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            databaseId = database.DatabaseId;
        }

        var historyId = new HistoryId(databaseId);
        using (var roots = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId))
        {
            roots.AppendCreate(new HistoryRootStoreRecord(
                HistoryRootStoreRecordType.Create,
                EventSequence: 0,
                HistoryRootId.New(),
                RootKind: 1,
                RootState: 2,
                databaseId,
                historyId,
                HistoryId.Empty,
                CommitSequence.Initial,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        Assert.Equal(0, reopened.GetDiagnostics().RetainingRootCount);
    }
}
