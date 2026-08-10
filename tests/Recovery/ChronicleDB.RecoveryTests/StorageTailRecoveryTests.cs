using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Pages;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.RecoveryTests;

public sealed class StorageTailRecoveryTests
{
    [Fact]
    public void CorruptPageInsideLatestCommitAppendRegionIsRebuiltFromWal()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [11]);
            database.Put([2], [22]);
        }

        var path = Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        var bytes = File.ReadAllBytes(path);
        var secondPageOffset = StorageOptions.DefaultPageSize;
        bytes[secondPageOffset + PageCodec.Size] ^= 0x7F;
        File.WriteAllBytes(path, bytes);

        using var recovered = ChronicleDatabase.Open(directory.Path);
        Assert.True(recovered.TryGet([1], out var first));
        Assert.True(recovered.TryGet([2], out var second));
        Assert.Equal(new byte[] { 11 }, first);
        Assert.Equal(new byte[] { 22 }, second);
    }

    [Fact]
    public void CorruptionOlderThanLatestCommitRecoveryBaseRemainsFatal()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [11]);
            database.Put([2], [22]);
        }

        var path = Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        var bytes = File.ReadAllBytes(path);
        bytes[PageCodec.Size] ^= 0x7F;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<StorageCorruptionException>(() => ChronicleDatabase.Open(directory.Path));
    }

    [Fact]
    public void UnrecoverablePartialTailIsNotDestructivelyTruncated()
    {
        using var directory = new StorageTestDirectory();
        using (ChronicleDatabase.Open(directory.Path))
        {
        }

        var path = Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.Throws<StorageCorruptionException>(() => ChronicleDatabase.Open(directory.Path));
        Assert.Equal(5L, new FileInfo(path).Length);
    }

    [Fact]
    public void LegacyWalWithoutRecoveryBaseDoesNotHideFullPageCorruption()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
            store.Put(new ChronicleDB.Core.Keys.BinaryKey([1]), [11]);
        }

        using (var wal = WalLog.Open(directory.Path, databaseId, new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            var transactionId = TransactionId.New();
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(WalRecordType.Commit, transactionId, []);
            wal.Flush();
        }

        var path = Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        var bytes = File.ReadAllBytes(path);
        bytes[PageCodec.Size] ^= 0x7F;
        File.WriteAllBytes(path, bytes);
        var lengthBeforeOpen = new FileInfo(path).Length;

        Assert.Throws<StorageCorruptionException>(() => ChronicleDatabase.Open(directory.Path));
        Assert.Equal(lengthBeforeOpen, new FileInfo(path).Length);
    }
}
