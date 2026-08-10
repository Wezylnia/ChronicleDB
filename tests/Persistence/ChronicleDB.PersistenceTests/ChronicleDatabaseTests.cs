using ChronicleDB;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ChronicleDatabaseTests
{
    [Fact]
    public void PutGetDeleteAndReopenPreserveLogicalState()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x01, 0x00, 0xFF };
        var value = new byte[] { 0x10, 0x20, 0x30 };
        Guid databaseId;

        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            databaseId = database.DatabaseId;
            database.Put(key, value);
            Assert.True(database.TryGet(key, out var stored));
            Assert.Equal(value, stored);
            Assert.True(database.Delete(key));
            Assert.False(database.TryGet(key, out _));
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        Assert.Equal(databaseId, reopened.DatabaseId);
        Assert.Equal(0, reopened.Count);
        Assert.False(reopened.TryGet(key, out _));
    }

    [Fact]
    public void EmptyValueAndOverflowValueSurviveReopen()
    {
        using var directory = new StorageTestDirectory();
        var emptyKey = new byte[] { 1 };
        var overflowKey = new byte[] { 2 };
        var overflowValue = Enumerable.Range(0, 40_000).Select(value => (byte)(value % 251)).ToArray();

        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put(emptyKey, []);
            database.Put(overflowKey, overflowValue);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        Assert.True(reopened.TryGet(emptyKey, out var emptyValue));
        Assert.Empty(emptyValue);
        Assert.True(reopened.TryGet(overflowKey, out var storedOverflow));
        Assert.Equal(overflowValue, storedOverflow);
    }

    [Fact]
    public void BulkAppendAndReopenPreserveDeterministicDataset()
    {
        using var directory = new StorageTestDirectory();
        const int entryCount = 5_000;
        var options = new ChronicleDB.Storage.StorageOptions { FlushOnWrite = false };

        using (var database = ChronicleDatabase.Open(directory.Path, options))
        {
            for (var index = 0; index < entryCount; index++)
            {
                database.Put(
                    BitConverter.GetBytes(index),
                    BitConverter.GetBytes(index * 17));
            }

            Assert.Equal(entryCount, database.Count);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path, options);
        Assert.Equal(entryCount, reopened.Count);
        foreach (var index in new[] { 0, 1, 127, 1024, entryCount - 1 })
        {
            Assert.True(reopened.TryGet(BitConverter.GetBytes(index), out var value));
            Assert.Equal(BitConverter.GetBytes(index * 17), value);
        }
    }

    [Fact]
    public void ConfiguredKeyAndValueLimitsAreEnforced()
    {
        using var directory = new StorageTestDirectory();
        var options = new ChronicleDB.Storage.StorageOptions { MaxKeySize = 2, MaxValueSize = 3 };

        using var database = ChronicleDatabase.Open(directory.Path, options);
        Assert.Throws<ChronicleDB.Storage.StorageLimitException>(() => database.Put([1, 2, 3], [1]));
        Assert.Throws<ChronicleDB.Storage.StorageLimitException>(() => database.Put([1], [1, 2, 3, 4]));
    }

    [Fact]
    public void EmptyKeyAndBinaryPayloadsRoundTripWithoutAliasing()
    {
        using var directory = new StorageTestDirectory();
        var sourceKey = new byte[] { 0x00, 0xFF, 0x00 };
        var sourceValue = new byte[] { 0x10, 0x20, 0x30 };

        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put([], [0x7F]);
        database.Put(sourceKey, sourceValue);
        sourceKey[0] = 0xEE;
        sourceValue[0] = 0xEE;

        Assert.True(database.TryGet([], out var emptyKeyValue));
        Assert.Equal(new byte[] { 0x7F }, emptyKeyValue);
        Assert.True(database.TryGet([0x00, 0xFF, 0x00], out var stored));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, stored);

        stored[1] = 0xEE;
        Assert.True(database.TryGet([0x00, 0xFF, 0x00], out var reread));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, reread);
    }

    [Fact]
    public void UpdatingAKeyKeepsOneLogicalRecordAndLatestValue()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDatabase.Open(directory.Path);

        database.Put([1], [1]);
        database.Put([1], [2, 3]);
        database.Put([1], [4, 5, 6]);

        Assert.Equal(1, database.Count);
        Assert.True(database.TryGet([1], out var value));
        Assert.Equal(new byte[] { 4, 5, 6 }, value);
    }

    [Fact]
    public void DeletingMissingKeyIsIdempotent()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDatabase.Open(directory.Path);

        Assert.False(database.Delete([99]));
        Assert.False(database.Delete([99]));
        Assert.Equal(0, database.Count);
    }
}
