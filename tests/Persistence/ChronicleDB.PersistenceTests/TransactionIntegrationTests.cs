using ChronicleDB;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.PersistenceTests;

public sealed class TransactionIntegrationTests
{
    [Fact]
    public void MultiKeyCommitPublishesAllMutationsAsOneLockedBatch()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        database.Put([2], [20]);

        using var transaction = database.BeginTransaction();
        transaction.Put([1], [11]);
        transaction.Delete([2]);
        transaction.Put([3], [30]);

        Assert.True(transaction.TryGet([1], out var localValue));
        Assert.Equal(new byte[] { 11 }, localValue);
        Assert.False(transaction.TryGet([2], out _));
        Assert.False(database.TryGet([3], out _));
        Assert.True(database.TryGet([1], out var beforeCommit));
        Assert.Equal(new byte[] { 10 }, beforeCommit);

        transaction.Commit();

        Assert.True(database.TryGet([1], out var committedValue));
        Assert.Equal(new byte[] { 11 }, committedValue);
        Assert.False(database.TryGet([2], out _));
        Assert.True(database.TryGet([3], out var insertedValue));
        Assert.Equal(new byte[] { 30 }, insertedValue);
    }

    [Fact]
    public void CommitWritesBeginMutationsAndCommitInWalOrder()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            using var transaction = database.BeginTransaction();
            transaction.Put([1], [2]);
            transaction.Delete([3]);
            transaction.Commit();
        }

        using var log = WalLog.Open(directory.Path);
        var records = log.ReadAll();
        Assert.Equal(
            [WalRecordType.Begin, WalRecordType.Put, WalRecordType.Delete, WalRecordType.Commit],
            records.Select(record => record.Type).ToArray());
        Assert.Equal(2, WalMutationCodec.DecodePut(records[1].Payload.Span).Value.ToArray()[0]);
    }

    [Fact]
    public void AbortedTransactionDoesNotPublishOrWriteWalRecords()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            using var transaction = database.BeginTransaction();
            transaction.Put([8], [9]);
            transaction.Abort();
            Assert.False(database.TryGet([8], out _));
        }

        using var log = WalLog.Open(directory.Path);
        Assert.Empty(log.ReadAll());
    }

    [Fact]
    public void MutationPayloadCodecRejectsLengthMismatchAndPreservesBinaryKeys()
    {
        var key = new ChronicleDB.Core.Keys.BinaryKey([0, 255, 0]);
        var payload = WalMutationCodec.EncodePut(key, [4, 5, 6]);
        var decoded = WalMutationCodec.DecodePut(payload);
        Assert.Equal(key, decoded.Key);
        Assert.Equal(new byte[] { 4, 5, 6 }, decoded.Value.ToArray());

        payload[2] = 0xFF;
        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() => WalMutationCodec.DecodePut(payload));
    }

    [Fact]
    public void DatabaseRejectsNewOperationsAfterDispose()
    {
        using var directory = new StorageTestDirectory();
        var database = ChronicleDatabase.Open(directory.Path);
        database.Dispose();

        Assert.Throws<ObjectDisposedException>(() => database.BeginTransaction());
        Assert.Throws<ObjectDisposedException>(() => database.Put([1], [1]));
        Assert.Throws<ObjectDisposedException>(() => database.TryGet([1], out _));
    }
}
