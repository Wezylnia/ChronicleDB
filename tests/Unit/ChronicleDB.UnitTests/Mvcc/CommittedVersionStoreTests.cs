using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Indexing.Baseline;
using ChronicleDB.Storage.Files;
using ChronicleDB.Transactions;
using ChronicleDB.Transactions.Mvcc;

namespace ChronicleDB.UnitTests.Mvcc;

public sealed class CommittedVersionStoreTests
{
    [Fact]
    public void ReadsNewestVersionVisibleAtBoundary()
    {
        var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        Publish(store, 1, [1], [10]);
        Publish(store, 2, [1], [20]);
        Publish(store, 3, [1], [30]);

        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(1), out var first));
        Assert.Equal(new byte[] { 10 }, first);
        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(2), out var second));
        Assert.Equal(new byte[] { 20 }, second);
        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(3), out var third));
        Assert.Equal(new byte[] { 30 }, third);
    }

    [Fact]
    public void TombstoneHidesOnlyBoundariesAtOrAfterDelete()
    {
        var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        Publish(store, 1, [4], [44]);

        var deleting = new Transaction();
        deleting.Begin();
        deleting.Delete([4]);
        store.PublishCommitted(TransactionId.New(), new CommitSequence(2), deleting.GetWriteSet());

        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([4]), new CommitSequence(1), out var oldValue));
        Assert.Equal(new byte[] { 44 }, oldValue);
        Assert.False(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([4]), new CommitSequence(2), out _));
        Assert.True(store.TryGetLatestCommitSequence(new ChronicleDB.Core.Keys.BinaryKey([4]), out var latest));
        Assert.Equal(new CommitSequence(2), latest);
    }

    [Fact]
    public void BoundaryBeforeFirstVersionDoesNotSeeFutureValue()
    {
        var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        Publish(store, 5, [8], [80]);

        Assert.False(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([8]), new CommitSequence(4), out _));
    }

    [Fact]
    public void RecoveryReplayReconstructsHistoricalChainInCommitOrder()
    {
        var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        var key = new ChronicleDB.Core.Keys.BinaryKey([6]);
        store.ReplayCommitted(
            TransactionId.New(),
            new CommitSequence(3),
            [new StorageMutation(key, isDelete: false, [30])]);
        store.ReplayCommitted(
            TransactionId.New(),
            new CommitSequence(7),
            [new StorageMutation(key, isDelete: false, [70])]);

        Assert.True(store.TryRead(key, new CommitSequence(3), out var oldValue));
        Assert.Equal(new byte[] { 30 }, oldValue);
        Assert.True(store.TryRead(key, new CommitSequence(7), out var newValue));
        Assert.Equal(new byte[] { 70 }, newValue);
    }

    private static void Publish(
        CommittedVersionStore store,
        ulong sequence,
        byte[] key,
        byte[] value)
    {
        var transaction = new Transaction();
        transaction.Begin();
        transaction.Put(key, value);
        store.PublishCommitted(TransactionId.New(), new CommitSequence(sequence), transaction.GetWriteSet());
    }
}
