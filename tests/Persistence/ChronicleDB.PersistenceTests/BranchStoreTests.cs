using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.PersistenceTests;

public sealed class BranchStoreTests
{
    [Fact]
    public void LifecycleAndCommitDescriptorsRoundTripAcrossRestart()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var main = new HistoryId(databaseId);
        var branchId = BranchId.New();
        var historyId = HistoryId.New();
        var rootId = HistoryRootId.New();
        var storageId = Guid.NewGuid();
        var transactionId = TransactionId.New();

        using (var store = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main))
        {
            store.AppendCreateIntent(
                branchId,
                historyId,
                main,
                rootId,
                new CommitSequence(7),
                100,
                depth: 1,
                "feature/a");
            store.AppendActivate(branchId, storageId);
            store.AppendAdvance(branchId, new CommitSequence(1), transactionId, 2, 32768);

            var active = Assert.Single(store.ListActive());
            Assert.Equal(new CommitSequence(1), active.LocalCommitSequence);
            var commit = Assert.Single(store.ListCommits(branchId));
            Assert.Equal(transactionId, commit.TransactionId);
            Assert.Equal(2, commit.MutationCount);
            Assert.Equal(32768, commit.DataLengthAfterCommit);
        }

        using var reopened = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main);
        var recovered = Assert.Single(reopened.ListActive());
        Assert.Equal(branchId, recovered.BranchId);
        Assert.Equal(historyId, recovered.HistoryId);
        Assert.Equal(main, recovered.ParentHistoryId);
        Assert.Equal(rootId, recovered.BaseRootId);
        Assert.Equal(storageId, recovered.LocalStorageId);
        Assert.Equal(new CommitSequence(1), recovered.LocalCommitSequence);
        Assert.Single(reopened.ListCommits(branchId));
    }

    [Fact]
    public void IncompleteFinalFrameIsTruncatedWithoutPublishingTransition()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var main = new HistoryId(databaseId);
        var branchId = BranchId.New();
        using (var store = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main))
        {
            store.AppendCreateIntent(
                branchId,
                HistoryId.New(),
                main,
                HistoryRootId.New(),
                CommitSequence.Initial,
                100,
                1,
                "partial");
        }

        var path = Path.Combine(directory.Path, PersistentBranchMetadataStore.FileName);
        var validLength = new FileInfo(path).Length;
        File.AppendAllBytes(path, [1, 2, 3, 4, 5]);

        using var reopened = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main);
        Assert.Single(reopened.ListCreating());
        Assert.Equal(validLength, new FileInfo(path).Length);
    }

    [Fact]
    public void CompleteCorruptFrameIsRejected()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var main = new HistoryId(databaseId);
        using (var store = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main))
        {
            store.AppendCreateIntent(
                BranchId.New(),
                HistoryId.New(),
                main,
                HistoryRootId.New(),
                CommitSequence.Initial,
                100,
                1,
                "corrupt");
        }

        var path = Path.Combine(directory.Path, PersistentBranchMetadataStore.FileName);
        var bytes = File.ReadAllBytes(path);
        bytes[^12] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<StorageCorruptionException>(
            () => PersistentBranchMetadataStore.Open(directory.Path, databaseId, main));
    }

    [Fact]
    public void PreWriteFaultDoesNotPublishIntentOrFaultStore()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var main = new HistoryId(databaseId);
        var injector = new ThrowingBranchFaultInjector(StorageFaultPoint.BeforeBranchMetadataRecordWrite);
        using var store = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main, injector);

        Assert.Throws<InvalidOperationException>(() => store.AppendCreateIntent(
            BranchId.New(),
            HistoryId.New(),
            main,
            HistoryRootId.New(),
            CommitSequence.Initial,
            100,
            1,
            "prewrite"));
        Assert.Empty(store.ListCreating());
        Assert.False(store.IsFaulted);
    }

    [Fact]
    public void AbandonedCreationReleasesNameButNeverReusesIdentity()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var main = new HistoryId(databaseId);
        var branchId = BranchId.New();
        var historyId = HistoryId.New();
        using var store = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main);
        store.AppendCreateIntent(
            branchId,
            historyId,
            main,
            HistoryRootId.New(),
            CommitSequence.Initial,
            100,
            1,
            "retryable");
        store.AppendAbandonCreate(branchId);

        store.EnsureNameAvailable("retryable");
        Assert.Throws<StorageException>(() => store.AppendCreateIntent(
            branchId,
            HistoryId.New(),
            main,
            HistoryRootId.New(),
            CommitSequence.Initial,
            101,
            1,
            "another"));
    }


    [Fact]
    public void TransactionIdentityCannotBeCommittedTwiceInOneBranchHistory()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var main = new HistoryId(databaseId);
        var branchId = BranchId.New();
        var transactionId = TransactionId.New();
        using var store = PersistentBranchMetadataStore.Open(directory.Path, databaseId, main);
        store.AppendCreateIntent(
            branchId,
            HistoryId.New(),
            main,
            HistoryRootId.New(),
            CommitSequence.Initial,
            100,
            1,
            "unique-tx");
        store.AppendActivate(branchId, Guid.NewGuid());
        store.AppendAdvance(branchId, new CommitSequence(1), transactionId, 0, 0);

        Assert.Throws<StorageException>(() =>
            store.ValidateAdvance(branchId, new CommitSequence(2), transactionId, 0, 0));
    }

    [Fact]
    public void BranchVersionEnvelopeRoundTripsValueAndTombstone()
    {
        var valueRecord = new BranchVersionRecord(
            BranchId.New(),
            HistoryId.New(),
            TransactionId.New(),
            new CommitSequence(3),
            0,
            1,
            [1, 2],
            IsDelete: false,
            [9, 8, 7]);
        var decoded = BranchVersionRecordCodec.Decode(BranchVersionRecordCodec.Encode(valueRecord));
        Assert.Equal(valueRecord.BranchId, decoded.BranchId);
        Assert.Equal(valueRecord.HistoryId, decoded.HistoryId);
        Assert.Equal(valueRecord.TransactionId, decoded.TransactionId);
        Assert.Equal(valueRecord.CommitSequence, decoded.CommitSequence);
        Assert.Equal(valueRecord.Key, decoded.Key);
        Assert.Equal(valueRecord.Value, decoded.Value);

        var tombstone = valueRecord with
        {
            TransactionId = TransactionId.New(),
            CommitSequence = new CommitSequence(4),
            IsDelete = true,
            Value = [],
        };
        var decodedDelete = BranchVersionRecordCodec.Decode(BranchVersionRecordCodec.Encode(tombstone));
        Assert.True(decodedDelete.IsDelete);
        Assert.Empty(decodedDelete.Value);
    }

    private sealed class ThrowingBranchFaultInjector(StorageFaultPoint target) : IStorageFaultInjector
    {
        public void Hit(StorageFaultPoint point, PageId pageId)
        {
            if (point == target)
            {
                throw new InvalidOperationException($"Injected branch metadata fault at {point}.");
            }
        }
    }
}
