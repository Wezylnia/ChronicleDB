using ChronicleDB.Core.Keys;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage.Files;

namespace ChronicleDB.PersistenceTests;

public sealed class LegacyStorageUpgradeTests
{
    [Fact]
    public void PreWalCurrentStateIsBootstrappedIntoV03SnapshotBoundary()
    {
        using var directory = new StorageTestDirectory();
        using (var legacyStore = PersistentKeyValueStore.Open(directory.Path))
        {
            legacyStore.Put(new BinaryKey([1]), [10]);
        }

        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.Equal((ulong)1, database.CurrentCommitSequence.Value);
        Assert.True(database.TryGet([1], out var current));
        Assert.Equal(new byte[] { 10 }, current);

        using var reader = database.BeginTransaction();
        database.Put([1], [20]);
        Assert.True(reader.TryGet([1], out var historical));
        Assert.Equal(new byte[] { 10 }, historical);
        Assert.True(database.TryGet([1], out var latest));
        Assert.Equal(new byte[] { 20 }, latest);
    }
}
