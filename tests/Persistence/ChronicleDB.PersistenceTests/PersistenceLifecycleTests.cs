using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.HistoryRoots;
using ChronicleDB.Storage.Snapshots;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.PersistenceTests;

public sealed class PersistenceLifecycleTests
{
    [Fact]
    public void DatabasePersistsCriticalSubsystemInitializationFlags()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [2]);
        }

        using var store = PersistentKeyValueStore.Open(directory.Path);
        Assert.True(store.HasFormatFlag(DatabaseHeader.WalInitializedFlag));
        Assert.True(store.HasFormatFlag(DatabaseHeader.SnapshotStoreInitializedFlag));
        Assert.True(store.HasFormatFlag(DatabaseHeader.HistoryRootStoreInitializedFlag));
        Assert.True(store.HasFormatFlag(DatabaseHeader.BranchStoreInitializedFlag));
        Assert.Equal(DatabaseHeader.SupportedFormatFlags, store.Header.FormatFlags);
        Assert.True(File.Exists(Path.Combine(directory.Path, PersistentHistoryRootStore.FileName)));
        Assert.True(File.Exists(Path.Combine(directory.Path, PersistentBranchMetadataStore.FileName)));
    }

    [Fact]
    public void MissingInitializedWalIsRejectedInsteadOfResettingHistory()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
        }

        File.Delete(Path.Combine(directory.Path, WalOptions.DefaultFileName));

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDB.ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void MissingInitializedSnapshotStoreIsRejectedInsteadOfForgettingRoots()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            using var snapshot = database.CreateSnapshot("must-survive");
        }

        File.Delete(Path.Combine(directory.Path, PersistentSnapshotStore.FileName));

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDB.ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void MissingInitializedHistoryRootStoreIsRejectedInsteadOfForgettingRoots()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
        }

        File.Delete(Path.Combine(directory.Path, PersistentHistoryRootStore.FileName));

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDB.ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void MissingInitializedBranchStoreIsRejectedInsteadOfForgettingBranches()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
        }

        File.Delete(Path.Combine(directory.Path, PersistentBranchMetadataStore.FileName));

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDB.ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void LegacyStoreWithoutInitializationFlagsCanUpgradeOnce()
    {
        using var directory = new StorageTestDirectory();
        using (var legacy = PersistentKeyValueStore.Open(directory.Path))
        {
            legacy.Put(new ChronicleDB.Core.Keys.BinaryKey([1]), [10]);
            Assert.Equal((uint)0, legacy.Header.FormatFlags);
        }

        using (var upgraded = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            Assert.True(upgraded.TryGet([1], out var value));
            Assert.Equal(new byte[] { 10 }, value);
        }

        Assert.True(File.Exists(Path.Combine(directory.Path, WalOptions.DefaultFileName)));
        Assert.True(File.Exists(Path.Combine(directory.Path, PersistentSnapshotStore.FileName)));
        Assert.True(File.Exists(Path.Combine(directory.Path, PersistentHistoryRootStore.FileName)));
        Assert.True(File.Exists(Path.Combine(directory.Path, PersistentBranchMetadataStore.FileName)));
        using var store = PersistentKeyValueStore.Open(directory.Path);
        Assert.Equal(DatabaseHeader.SupportedFormatFlags, store.Header.FormatFlags);
    }
    [Fact]
    public void OutOfBandPhysicalKeyAfterWalInitializationIsRejectedAsCorruption()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
        }

        using (var physical = PersistentKeyValueStore.Open(directory.Path))
        {
            physical.Put(new ChronicleDB.Core.Keys.BinaryKey([99]), [99]);
        }

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDB.ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void InterruptedUnflaggedUpgradeCanRetryLegacyBootstrap()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var legacy = PersistentKeyValueStore.Open(directory.Path))
        {
            legacy.Put(new ChronicleDB.Core.Keys.BinaryKey([1]), [10]);
            databaseId = legacy.DatabaseId;
        }

        // Simulate an earlier upgrade attempt that created WAL and wrote an incomplete
        // transaction but never durably published the metadata capability flag.
        using (var wal = WalLog.Open(
                   directory.Path,
                   databaseId,
                   new WalOptions { FlushOnAppend = false }))
        {
            var transactionId = ChronicleDB.Core.Identifiers.TransactionId.New();
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(
                WalRecordType.Put,
                transactionId,
                WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([2]), [20]));
            wal.Flush();
        }

        using (var beforeRetry = PersistentKeyValueStore.Open(directory.Path))
        {
            Assert.False(beforeRetry.HasFormatFlag(DatabaseHeader.WalInitializedFlag));
        }

        using var upgraded = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.True(upgraded.TryGet([1], out var legacyValue));
        Assert.Equal(new byte[] { 10 }, legacyValue);
        Assert.False(upgraded.TryGet([2], out _));
        Assert.Equal(upgraded.CurrentCommitSequence.Value, upgraded.HistoricalRetentionFloor);
    }

}
