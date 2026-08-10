using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
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
}
