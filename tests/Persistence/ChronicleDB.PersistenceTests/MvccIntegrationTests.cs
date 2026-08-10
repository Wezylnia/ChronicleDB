using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class MvccIntegrationTests
{
    [Fact]
    public void TransactionReadsRemainStableAfterLaterCommit()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);

        using var reader = database.BeginTransaction();
        Assert.Equal((ulong)1, reader.StartSequence);
        Assert.True(reader.TryGet([1], out var original));
        Assert.Equal(new byte[] { 10 }, original);

        using (var writer = database.BeginTransaction())
        {
            writer.Put([1], [20]);
            writer.Commit();
        }

        Assert.True(database.TryGet([1], out var latest));
        Assert.Equal(new byte[] { 20 }, latest);
        Assert.True(reader.TryGet([1], out var stable));
        Assert.Equal(new byte[] { 10 }, stable);
    }

    [Fact]
    public void TransactionDoesNotSeeKeyInsertedAfterItsStart()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var reader = database.BeginTransaction();

        database.Put([9], [90]);

        Assert.False(reader.TryGet([9], out _));
        Assert.True(database.TryGet([9], out var latest));
        Assert.Equal(new byte[] { 90 }, latest);
    }

    [Fact]
    public void TransactionOwnWritesOverrideSnapshot()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);

        using var transaction = database.BeginTransaction();
        transaction.Put([1], [99]);
        transaction.Put([2], [22]);
        transaction.Delete([2]);

        Assert.True(transaction.TryGet([1], out var ownValue));
        Assert.Equal(new byte[] { 99 }, ownValue);
        Assert.False(transaction.TryGet([2], out _));
        Assert.True(database.TryGet([1], out var committed));
        Assert.Equal(new byte[] { 10 }, committed);
    }

    [Fact]
    public void OldSnapshotStillSeesValueAfterLaterDelete()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([3], [33]);
        using var oldReader = database.BeginTransaction();

        Assert.True(database.Delete([3]));

        Assert.False(database.TryGet([3], out _));
        Assert.True(oldReader.TryGet([3], out var historical));
        Assert.Equal(new byte[] { 33 }, historical);
    }

    [Fact]
    public void FirstCommitterWinsSameKeyConflict()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [0]);
        using var first = database.BeginTransaction();
        using var second = database.BeginTransaction();
        first.Put([1], [1]);
        second.Put([1], [2]);

        first.Commit();
        var conflict = Assert.Throws<ChronicleDB.TransactionConflictException>(() => second.Commit());

        Assert.Equal((ulong)1, conflict.StartSequence);
        Assert.Equal((ulong)2, conflict.ConflictingSequence);
        Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
        Assert.True(database.TryGet([1], out var value));
        Assert.Equal(new byte[] { 1 }, value);
    }

    [Fact]
    public void NonOverlappingWritersFromSameSnapshotCanBothCommit()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var first = database.BeginTransaction();
        using var second = database.BeginTransaction();
        first.Put([1], [11]);
        second.Put([2], [22]);

        first.Commit();
        second.Commit();

        Assert.True(database.TryGet([1], out var firstValue));
        Assert.True(database.TryGet([2], out var secondValue));
        Assert.Equal(new byte[] { 11 }, firstValue);
        Assert.Equal(new byte[] { 22 }, secondValue);
    }

    [Fact]
    public void CommitSequencesSurviveRecoveryAndContinueMonotonically()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [1]);
            database.Put([2], [2]);
            Assert.Equal((ulong)2, database.CurrentCommitSequence.Value);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.Equal((ulong)2, reopened.CurrentCommitSequence.Value);
        using var transaction = reopened.BeginTransaction();
        Assert.Equal((ulong)2, transaction.StartSequence);
        transaction.Put([3], [3]);
        transaction.Commit();
        Assert.Equal((ulong)3, transaction.CommitSequence!.Value);
        Assert.Equal((ulong)3, reopened.CurrentCommitSequence.Value);
    }

    [Fact]
    public void SnapshotIsolationPermitsWriteSkewOnDisjointKeys()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        database.Put([2], [1]);

        using var first = database.BeginTransaction();
        using var second = database.BeginTransaction();
        Assert.True(first.TryGet([1], out var firstA));
        Assert.True(first.TryGet([2], out var firstB));
        Assert.True(second.TryGet([1], out var secondA));
        Assert.True(second.TryGet([2], out var secondB));
        Assert.Equal(new byte[] { 1 }, firstA);
        Assert.Equal(new byte[] { 1 }, firstB);
        Assert.Equal(new byte[] { 1 }, secondA);
        Assert.Equal(new byte[] { 1 }, secondB);

        first.Put([1], [0]);
        second.Put([2], [0]);
        first.Commit();
        second.Commit();

        Assert.True(database.TryGet([1], out var finalA));
        Assert.True(database.TryGet([2], out var finalB));
        Assert.Equal(new byte[] { 0 }, finalA);
        Assert.Equal(new byte[] { 0 }, finalB);
    }
}
