using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Files;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.RecoveryTests;

public sealed class MvccRecoveryTests
{
    [Fact]
    public void V03CommitSequenceIsRecoveredAndNextCommitAdvancesIt()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var transactionId = TransactionId.New();
        using (var wal = WalLog.Open(directory.Path, databaseId, new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            wal.Append(WalRecordType.Begin, transactionId, []);
            wal.Append(WalRecordType.Put, transactionId, WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([1]), [7]));
            wal.Append(WalRecordType.Commit, transactionId, WalCommitCodec.Encode(new CommitSequence(7), 0));
            wal.Flush();
        }

        using var database = ChronicleDatabase.Open(directory.Path);
        Assert.Equal(new CommitSequence(7), database.CurrentCommitSequence);
        using var next = database.BeginTransaction();
        next.Put([2], [8]);
        next.Commit();
        Assert.Equal((ulong)8, next.CommitSequence!.Value);
    }

    [Fact]
    public void NonIncreasingCommitSequenceIsRejected()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(directory.Path, databaseId, new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            AppendCommittedPut(wal, new CommitSequence(2), [1], [1], 0);
            AppendCommittedPut(wal, new CommitSequence(2), [2], [2], 0);
            wal.Flush();
        }

        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path));
    }

    [Fact]
    public void BeginPayloadIsRejectedDuringRecovery()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(directory.Path, databaseId))
        {
            wal.Append(WalRecordType.Begin, TransactionId.New(), [1]);
        }

        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path));
    }

    [Fact]
    public void LegacyPayloadlessPreResetWalMayCoexistWithCheckpointWhenItEndsAtBoundary()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(
                   directory.Path,
                   databaseId,
                   new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            AppendLegacyCommittedPut(wal, [1], [10]);
            AppendLegacyCommittedPut(wal, [2], [20]);
            wal.Flush();
        }

        using var recoveryStore = PersistentKeyValueStore.Open(
            directory.Path,
            options: null,
            allowIncompleteFinalPage: true);
        using var recoveryWal = WalLog.Open(directory.Path, databaseId);
        var result = ChronicleDB.Recovery.WalRecovery.Reconcile(
            recoveryStore,
            recoveryWal,
            checkpointSequence: new CommitSequence(2));

        Assert.Equal(new CommitSequence(2), result.CurrentCommitSequence);
        Assert.Empty(result.CommittedTransactions);
    }

    [Fact]
    public void PostResetWalMayContainOnlyCommitsNewerThanCheckpoint()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(
                   directory.Path,
                   databaseId,
                   new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            AppendCommittedPut(wal, new CommitSequence(3), [3], [30], 0);
            wal.Flush();
        }

        using var recoveryStore = PersistentKeyValueStore.Open(
            directory.Path,
            options: null,
            allowIncompleteFinalPage: true);
        using var recoveryWal = WalLog.Open(directory.Path, databaseId);
        var result = ChronicleDB.Recovery.WalRecovery.Reconcile(
            recoveryStore,
            recoveryWal,
            checkpointSequence: new CommitSequence(2));

        Assert.Equal(new CommitSequence(3), result.CurrentCommitSequence);
        var replay = Assert.Single(result.CommittedTransactions);
        Assert.Equal(new CommitSequence(3), replay.CommitSequence);
        Assert.Equal(new byte[] { 30 }, replay.Mutations.Single().Value.ToArray());
    }

    [Fact]
    public void PreResetWalThatDoesNotReachCheckpointIsRejected()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(
                   directory.Path,
                   databaseId,
                   new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            AppendCommittedPut(wal, new CommitSequence(1), [1], [10], 0);
            wal.Flush();
        }

        using var recoveryStore = PersistentKeyValueStore.Open(
            directory.Path,
            options: null,
            allowIncompleteFinalPage: true);
        using var recoveryWal = WalLog.Open(directory.Path, databaseId);
        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
            ChronicleDB.Recovery.WalRecovery.Reconcile(
                recoveryStore,
                recoveryWal,
                checkpointSequence: new CommitSequence(2)));
    }

    [Fact]
    public void WalCannotMixPreResetAndPostResetCheckpointGenerations()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(
                   directory.Path,
                   databaseId,
                   new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            AppendCommittedPut(wal, new CommitSequence(2), [2], [20], 0);
            AppendCommittedPut(wal, new CommitSequence(3), [3], [30], 0);
            wal.Flush();
        }

        using var recoveryStore = PersistentKeyValueStore.Open(
            directory.Path,
            options: null,
            allowIncompleteFinalPage: true);
        using var recoveryWal = WalLog.Open(directory.Path, databaseId);
        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
            ChronicleDB.Recovery.WalRecovery.Reconcile(
                recoveryStore,
                recoveryWal,
                checkpointSequence: new CommitSequence(2)));
    }

    [Fact]
    public void PostResetWalCannotReuseTransactionIdentityRetainedByCheckpoint()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        var retainedTransactionId = TransactionId.New();
        using (var wal = WalLog.Open(
                   directory.Path,
                   databaseId,
                   new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            wal.Append(WalRecordType.Begin, retainedTransactionId, []);
            wal.Append(
                WalRecordType.Put,
                retainedTransactionId,
                WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey([3]), [30]));
            wal.Append(
                WalRecordType.Commit,
                retainedTransactionId,
                WalCommitCodec.Encode(new CommitSequence(3), 0));
            wal.Flush();
        }

        using var recoveryStore = PersistentKeyValueStore.Open(
            directory.Path,
            options: null,
            allowIncompleteFinalPage: true);
        using var recoveryWal = WalLog.Open(directory.Path, databaseId);
        Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
            ChronicleDB.Recovery.WalRecovery.Reconcile(
                recoveryStore,
                recoveryWal,
                checkpointSequence: new CommitSequence(2),
                checkpointTransactionIds: new HashSet<TransactionId> { retainedTransactionId }));
        Assert.False(recoveryStore.TryGet(new ChronicleDB.Core.Keys.BinaryKey([3]), out _));
    }

    [Fact]
    public void RecoveryReturnsCompleteCommittedHistoryForVersionRebuild()
    {
        using var directory = new StorageTestDirectory();
        Guid databaseId;
        using (var store = PersistentKeyValueStore.Open(directory.Path))
        {
            databaseId = store.DatabaseId;
        }

        using (var wal = WalLog.Open(directory.Path, databaseId, new ChronicleDB.Wal.WalOptions { FlushOnAppend = false }))
        {
            AppendCommittedPut(wal, new CommitSequence(4), [1], [40], 0);
            AppendCommittedPut(wal, new CommitSequence(9), [1], [90], 0);
            wal.Flush();
        }

        using var recoveryStore = PersistentKeyValueStore.Open(
            directory.Path,
            options: null,
            allowIncompleteFinalPage: true);
        using var recoveryWal = WalLog.Open(directory.Path, databaseId);
        var result = ChronicleDB.Recovery.WalRecovery.Reconcile(recoveryStore, recoveryWal);

        Assert.Equal(new CommitSequence(9), result.CurrentCommitSequence);
        Assert.Equal(2, result.CommittedTransactions.Count);
        Assert.Equal(new CommitSequence(4), result.CommittedTransactions[0].CommitSequence);
        Assert.Equal(new CommitSequence(9), result.CommittedTransactions[1].CommitSequence);
        Assert.Equal(new byte[] { 40 }, result.CommittedTransactions[0].Mutations.Single().Value.ToArray());
        Assert.Equal(new byte[] { 90 }, result.CommittedTransactions[1].Mutations.Single().Value.ToArray());
    }

    private static void AppendLegacyCommittedPut(
        WalLog wal,
        byte[] key,
        byte[] value)
    {
        var transactionId = TransactionId.New();
        wal.Append(WalRecordType.Begin, transactionId, []);
        wal.Append(
            WalRecordType.Put,
            transactionId,
            WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey(key), value));
        wal.Append(WalRecordType.Commit, transactionId, []);
    }

    private static void AppendCommittedPut(
        WalLog wal,
        CommitSequence sequence,
        byte[] key,
        byte[] value,
        long baseDataLength)
    {
        var transactionId = TransactionId.New();
        wal.Append(WalRecordType.Begin, transactionId, []);
        wal.Append(
            WalRecordType.Put,
            transactionId,
            WalMutationCodec.EncodePut(new ChronicleDB.Core.Keys.BinaryKey(key), value));
        wal.Append(WalRecordType.Commit, transactionId, WalCommitCodec.Encode(sequence, baseDataLength));
    }
}
