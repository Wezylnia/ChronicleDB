using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Snapshots;

namespace ChronicleDB.PersistenceTests;

public sealed class SnapshotStoreTests
{
    [Fact]
    public void HeaderRoundTripPreservesDatabaseAndRetentionFloor()
    {
        var header = new SnapshotStoreHeader(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            new CommitSequence(17));

        var decoded = SnapshotStoreHeaderCodec.Decode(SnapshotStoreHeaderCodec.Encode(header));

        Assert.Equal(header, decoded);
    }

    [Fact]
    public void CreateAndDeleteRecordRoundTrips()
    {
        var id = new SnapshotId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var create = new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            1,
            id,
            new CommitSequence(9),
            1_700_000_000_000,
            "before-import");
        var delete = new SnapshotStoreRecord(
            SnapshotStoreRecordType.Delete,
            2,
            id,
            CommitSequence.Initial,
            0,
            string.Empty);

        Assert.Equal(create, SnapshotStoreRecordCodec.Decode(SnapshotStoreRecordCodec.Encode(create)));
        Assert.Equal(delete, SnapshotStoreRecordCodec.Decode(SnapshotStoreRecordCodec.Encode(delete)));
    }

    [Fact]
    public void RecordChecksumRejectsCompleteCorruption()
    {
        var record = new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            1,
            SnapshotId.New(),
            new CommitSequence(2),
            10,
            "stable");
        var bytes = SnapshotStoreRecordCodec.Encode(record);
        bytes[SnapshotStoreRecordCodec.HeaderSize] ^= 1;

        Assert.Throws<StorageCorruptionException>(() => SnapshotStoreRecordCodec.Decode(bytes));
    }

    [Fact]
    public void CreateRecordRejectsInvalidMetadataBeforePersistence()
    {
        var id = SnapshotId.New();
        Assert.Throws<StorageFormatException>(() => SnapshotStoreRecordCodec.Encode(new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            1,
            id,
            new CommitSequence(1),
            -1,
            "negative-time")));

        Assert.Throws<StorageFormatException>(() => SnapshotStoreRecordCodec.Encode(new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            1,
            id,
            new CommitSequence(1),
            1,
            "bad-\uD800")));

        Assert.Throws<StorageFormatException>(() => SnapshotStoreRecordCodec.Encode(new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            1,
            id,
            new CommitSequence(1),
            DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1,
            "too-late")));
    }

    [Fact]
    public void CreateDeleteLifecycleSurvivesReopen()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var first = SnapshotId.New();
        var second = SnapshotId.New();
        using (var store = PersistentSnapshotStore.Open(
                   directory.Path,
                   databaseId,
                   new CommitSequence(3)))
        {
            store.AppendCreate(first, new CommitSequence(3), 10, "first");
            store.AppendCreate(second, new CommitSequence(4), 11, "second");
            store.AppendDelete(first);
        }

        using var reopened = PersistentSnapshotStore.Open(
            directory.Path,
            databaseId,
            new CommitSequence(99));
        var snapshots = reopened.ListActive();
        var only = Assert.Single(snapshots);
        Assert.Equal(second, only.SnapshotId);
        Assert.Equal("second", only.Name);
        Assert.Equal(new CommitSequence(3), reopened.Header.RetentionFloor);
    }

    [Fact]
    public void IncompleteFinalRecordIsTruncatedWithoutInventingSnapshot()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        long validLength;
        using (var store = PersistentSnapshotStore.Open(
                   directory.Path,
                   databaseId,
                   CommitSequence.Initial))
        {
            store.AppendCreate(SnapshotId.New(), new CommitSequence(1), 1, "complete");
            validLength = store.FileLength;
        }

        var path = Path.Combine(directory.Path, PersistentSnapshotStore.FileName);
        var partial = SnapshotStoreRecordCodec.Encode(new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            2,
            SnapshotId.New(),
            new CommitSequence(1),
            2,
            "partial"));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(partial.AsSpan(0, SnapshotStoreRecordCodec.HeaderSize + 1));
        }

        using var reopened = PersistentSnapshotStore.Open(
            directory.Path,
            databaseId,
            CommitSequence.Initial);
        Assert.Equal(validLength, reopened.FileLength);
        Assert.Single(reopened.ListActive());
    }


    [Fact]
    public void CompleteRecordWithCorruptFrameLengthIsRejectedInsteadOfTruncated()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        using (var store = PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial))
        {
            store.AppendCreate(SnapshotId.New(), new CommitSequence(1), 1, "complete");
        }

        var path = Path.Combine(directory.Path, PersistentSnapshotStore.FileName);
        var originalLength = new FileInfo(path).Length;
        var bytes = File.ReadAllBytes(path);
        var recordOffset = SnapshotStoreHeaderCodec.Size;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(recordOffset + 12, 4), 999);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<StorageCorruptionException>(
            () => PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial));
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    [Fact]
    public void TruncatedCanonicalHeaderIsRejected()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        using (PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial))
        {
        }

        var path = Path.Combine(directory.Path, PersistentSnapshotStore.FileName);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SnapshotStoreHeaderCodec.Size - 1);
        }

        Assert.Throws<StorageCorruptionException>(
            () => PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial));
    }

    [Fact]
    public void StaleTemporaryCreationFileCannotOverrideCanonicalIdentity()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        using (PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial))
        {
        }

        File.WriteAllBytes(
            Path.Combine(directory.Path, PersistentSnapshotStore.FileName + ".creating"),
            [1, 2, 3]);

        using var reopened = PersistentSnapshotStore.Open(
            directory.Path,
            databaseId,
            CommitSequence.Initial);
        Assert.Equal(databaseId, reopened.Header.DatabaseId);
    }

    [Fact]
    public void DiscontinuousEventSequenceIsRejected()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        using (PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial))
        {
        }

        var path = Path.Combine(directory.Path, PersistentSnapshotStore.FileName);
        var record = SnapshotStoreRecordCodec.Encode(new SnapshotStoreRecord(
            SnapshotStoreRecordType.Create,
            2,
            SnapshotId.New(),
            CommitSequence.Initial,
            1,
            "gap"));
        File.AppendAllBytes(path, record);

        Assert.Throws<StorageCorruptionException>(
            () => PersistentSnapshotStore.Open(directory.Path, databaseId, CommitSequence.Initial));
    }

    [Fact]
    public void SnapshotStoreRejectsWrongDatabaseIdentity()
    {
        using var directory = new StorageTestDirectory();
        using (PersistentSnapshotStore.Open(directory.Path, Guid.NewGuid(), CommitSequence.Initial))
        {
        }

        Assert.Throws<StorageFormatException>(
            () => PersistentSnapshotStore.Open(directory.Path, Guid.NewGuid(), CommitSequence.Initial));
    }
}
