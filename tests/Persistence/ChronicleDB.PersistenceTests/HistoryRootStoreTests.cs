using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.HistoryRoots;

namespace ChronicleDB.PersistenceTests;

public sealed class HistoryRootStoreTests
{
    [Fact]
    public void HeaderAndLifecycleRecordsRoundTripAcrossRestart()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = HistoryId.New();
        var root = CreateRecord(databaseId, historyId);

        using (var store = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId))
        {
            store.AppendCreate(root);
            Assert.Single(store.ListRetaining());
            store.AppendDelete(root.RootId);
            Assert.Empty(store.ListRetaining());
        }

        using var reopened = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId);
        var deleted = Assert.Single(reopened.ListAll());
        Assert.Equal((byte)4, deleted.RootState);
        Assert.Equal((ulong)3, reopened.NextEventSequenceValue);
    }

    [Fact]
    public void IncompleteFinalRecordIsTruncatedWithoutInventingARoot()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = HistoryId.New();
        using (var store = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId))
        {
            store.AppendCreate(CreateRecord(databaseId, historyId));
        }

        var path = Path.Combine(directory.Path, PersistentHistoryRootStore.FileName);
        var originalLength = new FileInfo(path).Length;
        File.AppendAllBytes(path, [1, 2, 3, 4]);

        using var reopened = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId);
        Assert.Single(reopened.ListRetaining());
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    [Fact]
    public void CorruptCompleteRecordIsRejectedInsteadOfTruncated()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = HistoryId.New();
        using (var store = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId))
        {
            store.AppendCreate(CreateRecord(databaseId, historyId));
        }

        var path = Path.Combine(directory.Path, PersistentHistoryRootStore.FileName);
        var bytes = File.ReadAllBytes(path);
        bytes[^20] ^= 1;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<ChronicleDB.Storage.StorageCorruptionException>(
            () => PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId));
    }


    [Fact]
    public void BranchBaseRecordRoundTripsAndRequiresDistinctParentHistory()
    {
        var databaseId = Guid.NewGuid();
        var childHistory = HistoryId.New();
        var parentHistory = HistoryId.New();
        var record = new HistoryRootStoreRecord(
            HistoryRootStoreRecordType.Create,
            EventSequence: 7,
            HistoryRootId.New(),
            RootKind: 2,
            RootState: 2,
            databaseId,
            childHistory,
            parentHistory,
            new CommitSequence(11),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var decoded = HistoryRootStoreRecordCodec.Decode(HistoryRootStoreRecordCodec.Encode(record));
        Assert.Equal(childHistory, decoded.HistoryId);
        Assert.Equal(parentHistory, decoded.ParentHistoryId);
        Assert.Equal(new CommitSequence(11), decoded.Boundary);

        var invalid = record with { ParentHistoryId = childHistory };
        Assert.Throws<ChronicleDB.Storage.StorageFormatException>(
            () => HistoryRootStoreRecordCodec.Encode(invalid));
    }

    [Fact]
    public void PreWriteFaultDoesNotPublishARootOrFaultTheStore()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = HistoryId.New();
        var injector = new ThrowingRootFaultInjector(StorageFaultPoint.BeforeHistoryRootRecordWrite);
        using var store = PersistentHistoryRootStore.Open(directory.Path, databaseId, historyId, injector);

        Assert.Throws<InvalidOperationException>(() => store.AppendCreate(CreateRecord(databaseId, historyId)));
        Assert.Empty(store.ListRetaining());
        Assert.False(store.IsFaulted);
    }

    private static HistoryRootStoreRecord CreateRecord(Guid databaseId, HistoryId historyId)
        => new(
            HistoryRootStoreRecordType.Create,
            EventSequence: 0,
            HistoryRootId.New(),
            RootKind: 1,
            RootState: 2,
            databaseId,
            historyId,
            HistoryId.Empty,
            new CommitSequence(7),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class ThrowingRootFaultInjector(StorageFaultPoint target) : IStorageFaultInjector
    {
        public void Hit(StorageFaultPoint point, ChronicleDB.Core.Identifiers.PageId pageId)
        {
            if (point == target)
            {
                throw new InvalidOperationException($"Injected root fault at {point}.");
            }
        }
    }
}
