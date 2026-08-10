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
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
        }

        var transactionId = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path))
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
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
        }

        var incomplete = TransactionId.New();
        var aborted = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path))
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
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
        }

        var key = new ChronicleDB.Core.Keys.BinaryKey([9]);
        using (var wal = WalLog.Open(directory.Path))
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
    public void MutationAfterCommitIsRejectedAsMalformedTransaction()
    {
        using var directory = new StorageTestDirectory();
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
        }

        var transactionId = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path))
        {
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(WalRecordType.Commit, transactionId, []);
            wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([3]), [3]));
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
