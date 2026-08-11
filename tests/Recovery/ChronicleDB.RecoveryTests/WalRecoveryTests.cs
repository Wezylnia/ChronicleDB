using ChronicleDB;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage.Files;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.RecoveryTests;

public sealed class WalRecoveryTests
{
    [Fact]
    public void DurableCommitWithoutPhysicalPublicationIsRecovered()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var transactionId = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path, databaseId))
        {
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([1]), [2]));
            wal.Append(WalRecordType.Commit, transactionId, []);
        }

        using var database = ChronicleDatabase.Open(directory.Path);
        Assert.True(database.TryGet([1], out var value));
        Assert.Equal(new byte[] { 2 }, value);
    }

    [Fact]
    public void IncompleteAndAbortedTransactionsNeverBecomeVisible()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var incomplete = TransactionId.New();
        var aborted = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path, databaseId))
        {
            wal.Append(WalRecordType.Begin, incomplete, []);
            wal.Append(WalRecordType.Put, incomplete, WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([1]), [1]));
            wal.Append(WalRecordType.Begin, aborted, []);
            wal.Append(WalRecordType.Put, aborted, WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([2]), [2]));
            wal.Append(WalRecordType.Abort, aborted, []);
        }

        using var database = ChronicleDatabase.Open(directory.Path);
        Assert.False(database.TryGet([1], out _));
        Assert.False(database.TryGet([2], out _));
    }

    [Fact]
    public void RecoveryUsesLatestCommittedValueAndIsIdempotent()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var key = new ChronicleDB.Core.Keys.BinaryKey([9]);
        using (var wal = WalLog.Open(directory.Path, databaseId))
        {
            AppendCommittedPut(wal, key, [1]);
            AppendCommittedPut(wal, key, [2]);
        }

        long dataLengthAfterFirstRecovery;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            Assert.True(database.TryGet([9], out var value));
            Assert.Equal(new byte[] { 2 }, value);
            dataLengthAfterFirstRecovery = new FileInfo(Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName)).Length;
        }

        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            Assert.True(database.TryGet([9], out var value));
            Assert.Equal(new byte[] { 2 }, value);
        }

        var dataLengthAfterSecondRecovery = new FileInfo(Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName)).Length;
        Assert.Equal(dataLengthAfterFirstRecovery, dataLengthAfterSecondRecovery);
    }

    [Fact]
    public void RecoveryRejectsCommittedWalKeyBeyondConfiguredLogicalLimitBeforePhysicalRedo()
    {
        using var directory = new StorageTestDirectory();
        var options = new ChronicleDB.Storage.StorageOptions
        {
            MaxKeySize = 4,
            MaxValueSize = 32,
        };
        using var store = PersistentKeyValueStore.Open(directory.Path, options);
        var beforeLength = store.DataLength;
        var transactionId = TransactionId.New();
        var oversizedKey = new ChronicleDB.Core.Keys.BinaryKey(new byte[5]);

        using var wal = WalLog.Open(
            directory.Path,
            store.DatabaseId,
            new ChronicleDB.Wal.WalOptions { FlushOnAppend = false });
        wal.Append(WalRecordType.Begin, transactionId, []);
        wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(oversizedKey, [7]));
        wal.Append(WalRecordType.Commit, transactionId, []);
        wal.Flush();

        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
            ChronicleDB.Recovery.WalRecovery.Reconcile(store, wal));
        Assert.Equal(beforeLength, store.DataLength);
        Assert.False(store.TryGet(oversizedKey, out _));
    }

    [Fact]
    public void PreResetWalHistoryAtCheckpointIsNotReplayedAgainstCurrentLogicalLimits()
    {
        using var directory = new StorageTestDirectory();
        var options = new ChronicleDB.Storage.StorageOptions
        {
            MaxKeySize = 4,
            MaxValueSize = 32,
        };
        using var store = PersistentKeyValueStore.Open(directory.Path, options);
        var transactionId = TransactionId.New();
        var obsoleteKey = new ChronicleDB.Core.Keys.BinaryKey(new byte[5]);

        using var wal = WalLog.Open(
            directory.Path,
            store.DatabaseId,
            new ChronicleDB.Wal.WalOptions { FlushOnAppend = false });
        wal.Append(WalRecordType.Begin, transactionId, []);
        wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(obsoleteKey, [7]));
        wal.Append(
            WalRecordType.Commit,
            transactionId,
            WalCommitCodec.Encode(new ChronicleDB.Core.Sequences.CommitSequence(1), 0));
        wal.Flush();

        var result = ChronicleDB.Recovery.WalRecovery.Reconcile(
            store,
            wal,
            new ChronicleDB.Core.Sequences.CommitSequence(1),
            new HashSet<TransactionId>());

        Assert.Equal((ulong)1, result.CurrentCommitSequence.Value);
        Assert.Empty(result.CommittedTransactions);
        Assert.False(store.TryGet(obsoleteKey, out _));
    }

    [Fact]
    public void RecoveryRejectsCommittedWalValueBeyondConfiguredLogicalLimitBeforePhysicalRedo()
    {
        using var directory = new StorageTestDirectory();
        var options = new ChronicleDB.Storage.StorageOptions
        {
            MaxKeySize = 8,
            MaxValueSize = 4,
        };
        using var store = PersistentKeyValueStore.Open(directory.Path, options);
        var beforeLength = store.DataLength;
        var transactionId = TransactionId.New();
        var key = new ChronicleDB.Core.Keys.BinaryKey([1]);

        using var wal = WalLog.Open(
            directory.Path,
            store.DatabaseId,
            new ChronicleDB.Wal.WalOptions { FlushOnAppend = false });
        wal.Append(WalRecordType.Begin, transactionId, []);
        wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(key, new byte[5]));
        wal.Append(WalRecordType.Commit, transactionId, []);
        wal.Flush();

        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
            ChronicleDB.Recovery.WalRecovery.Reconcile(store, wal));
        Assert.Equal(beforeLength, store.DataLength);
        Assert.False(store.TryGet(key, out _));
    }

    [Fact]
    public void MutationAfterCommitIsRejectedAsMalformedTransaction()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var transactionId = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path, databaseId))
        {
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(WalRecordType.Commit, transactionId, []);
            wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([3]), [3]));
        }

        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() => ChronicleDatabase.Open(directory.Path));
    }

    [Fact]
    public void ReusedTransactionIdIsRejected()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var transactionId = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path, databaseId))
        {
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(WalRecordType.Commit, transactionId, []);
            wal.Append(WalRecordType.Begin, transactionId, []);
        }

        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() => ChronicleDatabase.Open(directory.Path));
    }

    private static void AppendCommittedPut(WalLog wal, ChronicleDB.Core.Keys.BinaryKey key, ReadOnlySpan<byte> value)
    {
        var transactionId = TransactionId.New();
        wal.Append(WalRecordType.Begin, transactionId, []);
        wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(key, value));
        wal.Append(WalRecordType.Commit, transactionId, []);
    }
}

internal sealed class StorageTestDirectory : IDisposable
{
    public StorageTestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronicle-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
