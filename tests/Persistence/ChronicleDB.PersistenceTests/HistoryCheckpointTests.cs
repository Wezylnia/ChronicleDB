using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.History;

namespace ChronicleDB.PersistenceTests;

public sealed class HistoryCheckpointTests
{
    [Fact]
    public void CheckpointRoundTripPreservesIdentityFloorAndVersions()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(3),
            new CommitSequence(2),
            [
                new HistoryCheckpointVersion(
                    new TransactionId(Guid.NewGuid()),
                    new CommitSequence(2),
                    new BinaryKey([1]),
                    false,
                    new byte[] { 10 }),
                new HistoryCheckpointVersion(
                    new TransactionId(Guid.NewGuid()),
                    new CommitSequence(3),
                    new BinaryKey([2]),
                    true,
                    ReadOnlyMemory<byte>.Empty),
            ]);

        var written = PersistentHistoryCheckpoint.Publish(directory.Path, checkpoint);
        var recovered = PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId);

        Assert.NotNull(recovered);
        Assert.True(written > 0);
        Assert.Equal(checkpoint.CheckpointSequence, recovered!.CheckpointSequence);
        Assert.Equal(checkpoint.RetentionFloor, recovered.RetentionFloor);
        Assert.Equal(2, recovered.Versions.Count);
        Assert.Equal(new byte[] { 10 }, recovered.Versions[0].Value.ToArray());
        Assert.True(recovered.Versions[1].IsDelete);
    }


    [Fact]
    public void CheckpointRoundTripSupportsEmptyBinaryKey()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [
                new HistoryCheckpointVersion(
                    new TransactionId(Guid.NewGuid()),
                    new CommitSequence(1),
                    new BinaryKey([]),
                    false,
                    new byte[] { 9 }),
            ]);

        _ = PersistentHistoryCheckpoint.Publish(directory.Path, checkpoint);
        var recovered = PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId);

        Assert.NotNull(recovered);
        var version = Assert.Single(recovered!.Versions);
        Assert.Equal(0, version.Key.Length);
        Assert.Equal(new byte[] { 9 }, version.Value.ToArray());
    }

    [Fact]
    public void CheckpointRejectsRecordWhoseDeclaredPayloadExceedsRemainingFile()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [
                new HistoryCheckpointVersion(
                    new TransactionId(Guid.NewGuid()),
                    new CommitSequence(1),
                    new BinaryKey([1]),
                    false,
                    new byte[] { 1 }),
            ]);
        _ = PersistentHistoryCheckpoint.Publish(directory.Path, checkpoint);

        var path = Path.Combine(directory.Path, PersistentHistoryCheckpoint.FileName);
        var bytes = File.ReadAllBytes(path);
        const uint declaredValueLength = 8 * 1024 * 1024;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80 + 8, 4), 64 + 1 + declaredValueLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80 + 44, 4), declaredValueLength);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<StorageCorruptionException>(() =>
            PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId));
    }

    [Fact]
    public void CheckpointRejectsImpossibleRecordCountBeforeAllocating()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [
                new HistoryCheckpointVersion(
                    new TransactionId(Guid.NewGuid()),
                    new CommitSequence(1),
                    new BinaryKey([1]),
                    false,
                    new byte[] { 1 }),
            ]);
        _ = PersistentHistoryCheckpoint.Publish(directory.Path, checkpoint);

        var path = Path.Combine(directory.Path, PersistentHistoryCheckpoint.FileName);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64, 4), int.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(76, 4),
            ChronicleDB.Storage.Formats.Crc32C.Compute(bytes.AsSpan(0, 76)));
        File.WriteAllBytes(path, bytes);

        Assert.Throws<StorageCorruptionException>(() =>
            PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId));
    }

    [Fact]
    public void CheckpointRejectsWrongHistoryAndByteCorruption()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [
                new HistoryCheckpointVersion(
                    new TransactionId(Guid.NewGuid()),
                    new CommitSequence(1),
                    new BinaryKey([1]),
                    false,
                    new byte[] { 1 }),
            ]);
        _ = PersistentHistoryCheckpoint.Publish(directory.Path, checkpoint);

        Assert.Throws<StorageCorruptionException>(() =>
            PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, new HistoryId(Guid.NewGuid())));

        var path = Path.Combine(directory.Path, PersistentHistoryCheckpoint.FileName);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x7f;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<StorageCorruptionException>(() =>
            PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId));
    }

    [Fact]
    public void ValidatedPrimaryCheckpointTakesPrecedenceOverOlderPreviousGeneration()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var transactionId = new TransactionId(Guid.NewGuid());
        var first = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [new HistoryCheckpointVersion(transactionId, new CommitSequence(1), new BinaryKey([1]), false, new byte[] { 1 })]);
        var second = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(2),
            CommitSequence.Initial,
            [
                new HistoryCheckpointVersion(transactionId, new CommitSequence(1), new BinaryKey([1]), false, new byte[] { 1 }),
                new HistoryCheckpointVersion(new TransactionId(Guid.NewGuid()), new CommitSequence(2), new BinaryKey([1]), false, new byte[] { 2 }),
            ]);

        _ = PersistentHistoryCheckpoint.Publish(directory.Path, first);
        var path = Path.Combine(directory.Path, PersistentHistoryCheckpoint.FileName);
        var olderBytes = File.ReadAllBytes(path);
        _ = PersistentHistoryCheckpoint.Publish(directory.Path, second);
        File.WriteAllBytes(path + ".previous", olderBytes);

        var recovered = PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId);

        Assert.NotNull(recovered);
        Assert.Equal(new CommitSequence(2), recovered!.CheckpointSequence);
        Assert.Equal(new byte[] { 2 }, recovered.Versions[^1].Value.ToArray());
    }

    [Fact]
    public void CorruptPresentPrimaryDoesNotRollBackToPreviousGeneration()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var first = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [new HistoryCheckpointVersion(new TransactionId(Guid.NewGuid()), new CommitSequence(1), new BinaryKey([1]), false, new byte[] { 1 })]);
        var second = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(2),
            CommitSequence.Initial,
            [new HistoryCheckpointVersion(new TransactionId(Guid.NewGuid()), new CommitSequence(2), new BinaryKey([1]), false, new byte[] { 2 })]);

        _ = PersistentHistoryCheckpoint.Publish(directory.Path, first);
        var path = Path.Combine(directory.Path, PersistentHistoryCheckpoint.FileName);
        var olderBytes = File.ReadAllBytes(path);
        _ = PersistentHistoryCheckpoint.Publish(directory.Path, second);
        File.WriteAllBytes(path + ".previous", olderBytes);

        var corruptPrimary = File.ReadAllBytes(path);
        corruptPrimary[^1] ^= 0x5a;
        File.WriteAllBytes(path, corruptPrimary);

        Assert.Throws<StorageCorruptionException>(
            () => PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId));
        Assert.Equal(olderBytes, File.ReadAllBytes(path + ".previous"));
    }

    [Fact]
    public void MissingPrimaryRestoresPreviousGenerationAfterInterruptedRename()
    {
        using var directory = new StorageTestDirectory();
        var databaseId = Guid.NewGuid();
        var historyId = new HistoryId(Guid.NewGuid());
        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            new CommitSequence(1),
            CommitSequence.Initial,
            [new HistoryCheckpointVersion(new TransactionId(Guid.NewGuid()), new CommitSequence(1), new BinaryKey([1]), false, new byte[] { 1 })]);

        _ = PersistentHistoryCheckpoint.Publish(directory.Path, checkpoint);
        var path = Path.Combine(directory.Path, PersistentHistoryCheckpoint.FileName);
        var previousPath = path + ".previous";
        File.Move(path, previousPath);

        var recovered = PersistentHistoryCheckpoint.TryLoad(directory.Path, databaseId, historyId);

        Assert.NotNull(recovered);
        Assert.Equal(new CommitSequence(1), recovered!.CheckpointSequence);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(previousPath));
    }


}
