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
    public void LatestReadUsesCurrentHeadAndPreservesTombstones()
    {
        var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        var key = new ChronicleDB.Core.Keys.BinaryKey([5]);
        Publish(store, 1, [5], [10]);
        Publish(store, 2, [5], [20]);

        Assert.True(store.TryReadLatest(key, out var latest));
        Assert.Equal(new byte[] { 20 }, latest);

        var deleting = new Transaction();
        deleting.Begin();
        deleting.Delete([5]);
        store.PublishCommitted(TransactionId.New(), new CommitSequence(3), deleting.GetWriteSet());

        Assert.False(store.TryReadLatest(key, out _));
        Assert.Equal(CommittedVersionResolutionKind.Tombstone, store.ResolveLatest(key).Kind);
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

    [Fact]
    public void RetentionProjectionPreservesGenericRangeAndExactPinnedBoundaries()
    {
        using var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        for (ulong sequence = 1; sequence <= 10; sequence++)
        {
            Publish(store, sequence, [1], [(byte)sequence]);
        }
        Publish(store, 3, [2], [30]);
        Publish(store, 9, [2], [90]);

        var pins = new[] { new CommitSequence(2), new CommitSequence(5), new CommitSequence(7) };
        var projection = store.CreateRetentionProjection(new CommitSequence(8), pins);

        var keyOneSequences = projection
            .Where(version => version.Key.AsSpan().SequenceEqual(new byte[] { 1 }))
            .Select(version => version.CommitSequence.Value)
            .ToArray();
        Assert.Equal(new ulong[] { 2, 5, 7, 8, 9, 10 }, keyOneSequences);

        var keyTwoSequences = projection
            .Where(version => version.Key.AsSpan().SequenceEqual(new byte[] { 2 }))
            .Select(version => version.CommitSequence.Value)
            .ToArray();
        Assert.Equal(new ulong[] { 3, 9 }, keyTwoSequences);

        _ = store.CompactHistory(new CommitSequence(8), pins);
        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(2), out var atTwo));
        Assert.Equal(new byte[] { 2 }, atTwo);
        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(7), out var atSeven));
        Assert.Equal(new byte[] { 7 }, atSeven);
        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(10), out var latest));
        Assert.Equal(new byte[] { 10 }, latest);
    }

    [Fact]
    public void ExactProjectionCanRemoveIntermediateHistoryWithoutChangingLatestState()
    {
        using var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        Publish(store, 1, [1], [10]);
        Publish(store, 2, [1], [20]);
        Publish(store, 3, [1], [30]);

        var history = store.SnapshotHistory();
        var projection = history.Where(version => version.CommitSequence.Value is 1 or 3).ToArray();
        var result = store.CompactHistoryToProjection(projection);

        Assert.Equal(1, result.ReclaimedVersions);
        Assert.Equal(2, result.RetainedVersions);
        Assert.True(store.TryReadLatest(new ChronicleDB.Core.Keys.BinaryKey([1]), out var latest));
        Assert.Equal(new byte[] { 30 }, latest);
        Assert.True(store.TryRead(new ChronicleDB.Core.Keys.BinaryKey([1]), new CommitSequence(2), out var historical));
        Assert.Equal(new byte[] { 10 }, historical);
    }

    [Fact]
    public void ExactProjectionRejectsRemovalOfLatestVersion()
    {
        using var store = new CommittedVersionStore(new SynchronizedVersionIndex());
        Publish(store, 1, [1], [10]);
        Publish(store, 2, [1], [20]);

        var projection = store.SnapshotHistory()
            .Where(version => version.CommitSequence.Value == 1)
            .ToArray();

        Assert.Throws<ArgumentException>(() => store.CompactHistoryToProjection(projection));
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
