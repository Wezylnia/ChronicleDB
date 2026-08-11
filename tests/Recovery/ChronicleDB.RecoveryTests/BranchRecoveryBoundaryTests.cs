using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.Files;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Branches;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.RecoveryTests;

public sealed class BranchRecoveryBoundaryTests
{
    [Fact]
    public void FailedLocalPublicationAfterDurableWalIsRedoneOnReopen()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedStorageFaultInjector(StorageFaultPoint.AfterPageWrite);
            Guid branchId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [10]);
                using var branch = database.CreateBranch("orphan-prefix");
                branchId = branch.BranchId;
                injector.Arm();
                using var transaction = branch.BeginTransaction();
                transaction.Put([1], [20]);
                Assert.Throws<InvalidOperationException>(transaction.Commit);
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            using var recovered = reopened.OpenBranch(branchId);
            Assert.Equal((ulong)1, recovered.CurrentSequence);
            Assert.True(recovered.TryGet([1], out var inherited));
            Assert.Equal(new byte[] { 20 }, inherited);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ChecksumCorruptionInsidePublishedBranchPrefixIsRebuiltFromAuthoritativeWal()
    {
        var directory = NewDirectory();
        try
        {
            Guid branchId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                database.Put([1], [10]);
                using var branch = database.CreateBranch("committed-prefix-corruption");
                branchId = branch.BranchId;
                branch.Put([1], [20]);
            }

            var branchDataPath = Path.Combine(
                directory,
                "branches",
                branchId.ToString("N"),
                PersistentKeyValueStore.DataFileName);
            var bytes = File.ReadAllBytes(branchDataPath);
            Assert.NotEmpty(bytes);
            bytes[Math.Min(100, bytes.Length - 1)] ^= 0x5A;
            File.WriteAllBytes(branchDataPath, bytes);

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            using var branchHandle = reopened.OpenBranch(branchId);
            Assert.True(branchHandle.TryGet([1], out var value));
            Assert.Equal(new byte[] { 20 }, value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void FaultAfterBranchMetadataFlushRecoversCommittedLocalTransaction()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedStorageFaultInjector(StorageFaultPoint.AfterBranchMetadataFlush);
            Guid branchId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [10]);
                using var branch = database.CreateBranch("ambiguous-ack");
                branchId = branch.BranchId;
                injector.Arm();
                using var transaction = branch.BeginTransaction();
                transaction.Put([1], [20]);
                Assert.Throws<InvalidOperationException>(transaction.Commit);
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            using var recovered = reopened.OpenBranch(branchId);
            Assert.Equal((ulong)1, recovered.CurrentSequence);
            Assert.True(recovered.TryGet([1], out var value));
            Assert.Equal(new byte[] { 20 }, value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }


    [Fact]
    public void BranchCreatePreWriteFailureLeavesDatabaseUsableAndNoBranch()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedNthStorageFaultInjector(
                StorageFaultPoint.BeforeBranchMetadataRecordWrite,
                hitNumber: 1);
            using var database = ChronicleDB.ChronicleDatabase.Open(
                directory,
                new StorageOptions { FaultInjector = injector });
            database.Put([1], [10]);
            injector.Arm();

            Assert.Throws<InvalidOperationException>(() => database.CreateBranch("prewrite"));
            Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
            Assert.Empty(database.ListBranches());
            Assert.True(database.TryGet([1], out var value));
            Assert.Equal(new byte[] { 10 }, value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DurableCreateIntentWithoutActivationIsAbandonedOnReopen()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedNthStorageFaultInjector(
                StorageFaultPoint.AfterBranchMetadataFlush,
                hitNumber: 1);
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.CreateBranch("intent-only"));
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.Empty(reopened.ListBranches());
            using var retry = reopened.CreateBranch("intent-only");
            Assert.Equal("intent-only", retry.Name);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DurableBaseRootWithoutActivationIsReleasedOnReopen()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedNthStorageFaultInjector(
                StorageFaultPoint.AfterHistoryRootFlush,
                hitNumber: 1);
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [10]);
                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.CreateBranch("root-only"));
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            var branchRoot = Path.Combine(directory, "branches");
            Assert.True(Directory.Exists(branchRoot));
            Assert.NotEmpty(Directory.EnumerateDirectories(branchRoot));

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.Empty(reopened.ListBranches());
            Assert.Empty(Directory.EnumerateDirectories(branchRoot));
            using var retry = reopened.CreateBranch("root-only");
            Assert.True(retry.TryGet([1], out var inherited));
            Assert.Equal(new byte[] { 10 }, inherited);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DurableActivationWithoutAcknowledgementReopensCompleteBranch()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedNthStorageFaultInjector(
                StorageFaultPoint.AfterBranchMetadataFlush,
                hitNumber: 2);
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [10]);
                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.CreateBranch("activated"));
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            using var branch = reopened.OpenBranch("activated");
            Assert.Equal((ulong)0, branch.CurrentSequence);
            Assert.True(branch.TryGet([1], out var inherited));
            Assert.Equal(new byte[] { 10 }, inherited);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void InitializedBranchWalIsMandatoryRecoveryAuthority()
    {
        var directory = NewDirectory();
        try
        {
            Guid branchId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                using var branch = database.CreateBranch("missing-wal");
                branchId = branch.BranchId;
                branch.Put([1], [20]);
            }

            File.Delete(Path.Combine(
                directory,
                "branches",
                branchId.ToString("N"),
                "branch.wal"));

            Assert.Throws<StorageCorruptionException>(
                () => ChronicleDB.ChronicleDatabase.Open(directory).Dispose());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RewrittenBranchPhysicalKeyThatDisagreesWithVersionIdentityIsRejected()
    {
        var directory = NewDirectory();
        try
        {
            Guid branchId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                using var branch = database.CreateBranch("physical-key-corruption");
                branchId = branch.BranchId;
                branch.Put([1], [20]);
            }

            var branchDirectory = Path.Combine(directory, "branches", branchId.ToString("N"));
            WalRecord commitRecord;
            WalCommitInfo commit;
            using (var wal = WalLog.Open(
                       branchDirectory,
                       new WalOptions { FileName = "branch.wal", FlushOnAppend = false }))
            {
                commitRecord = wal.ReadAll().Single(record => record.Type == WalRecordType.Commit);
                var envelope = BranchWalEnvelopeCodec.Decode(commitRecord.Payload.Span);
                commit = WalCommitCodec.Decode(envelope.Payload.Span);
            }

            var physicalKeyBytes = new byte[28];
            BinaryPrimitives.WriteUInt64LittleEndian(physicalKeyBytes.AsSpan(0, 8), commit.CommitSequence.Value);
            commitRecord.TransactionId.Value.TryWriteBytes(physicalKeyBytes.AsSpan(8, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(physicalKeyBytes.AsSpan(24, 4), 0);
            var expectedPhysicalKey = new BinaryKey(physicalKeyBytes);

            using (var store = PersistentKeyValueStore.Open(branchDirectory))
            {
                Assert.True(store.TryGet(expectedPhysicalKey, out var encodedVersion));
                var wrongPhysicalKey = new BinaryKey(Enumerable.Repeat((byte)0xA5, 28).ToArray());
                _ = store.RewriteState(
                [
                    new StorageMutation(wrongPhysicalKey, isDelete: false, encodedVersion),
                ]);
            }

            Assert.Throws<StorageCorruptionException>(
                () => ChronicleDB.ChronicleDatabase.Open(directory).Dispose());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void BranchWalGenerationBelowCheckpointMustReachTheCheckpointBoundary()
    {
        var directory = NewDirectory();
        try
        {
            var branchId = BranchId.New();
            var historyId = HistoryId.New();
            var localStorageId = Guid.NewGuid();
            using var wal = WalLog.Open(
                directory,
                localStorageId,
                new WalOptions { FileName = "branch.wal", FlushOnAppend = false });
            AppendBranchCommittedPut(wal, branchId, historyId, new CommitSequence(1), [1], [10]);
            wal.Flush();

            Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
                ChronicleDB.Recovery.BranchWalRecovery.ReadCommitted(
                    wal, branchId, historyId, new CommitSequence(2), 16 * 1024));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void BranchWalCannotMixCheckpointAndPostResetGenerations()
    {
        var directory = NewDirectory();
        try
        {
            var branchId = BranchId.New();
            var historyId = HistoryId.New();
            var localStorageId = Guid.NewGuid();
            using var wal = WalLog.Open(
                directory,
                localStorageId,
                new WalOptions { FileName = "branch.wal", FlushOnAppend = false });
            AppendBranchCommittedPut(wal, branchId, historyId, new CommitSequence(2), [1], [20]);
            AppendBranchCommittedPut(wal, branchId, historyId, new CommitSequence(3), [1], [30]);
            wal.Flush();

            Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
                ChronicleDB.Recovery.BranchWalRecovery.ReadCommitted(
                    wal, branchId, historyId, new CommitSequence(2), 16 * 1024));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void PostResetBranchWalCannotReuseTransactionIdentityRetainedByCheckpoint()
    {
        var directory = NewDirectory();
        try
        {
            var branchId = BranchId.New();
            var historyId = HistoryId.New();
            var localStorageId = Guid.NewGuid();
            var retainedTransactionId = TransactionId.New();
            using var wal = WalLog.Open(
                directory,
                localStorageId,
                new WalOptions { FileName = "branch.wal", FlushOnAppend = false });
            AppendBranchCommittedPut(
                wal,
                branchId,
                historyId,
                new CommitSequence(3),
                [1],
                [30],
                retainedTransactionId);
            wal.Flush();

            Assert.Throws<ChronicleDB.Wal.Errors.WalCorruptionException>(() =>
                ChronicleDB.Recovery.BranchWalRecovery.ReadCommitted(
                    wal,
                    branchId,
                    historyId,
                    new CommitSequence(2),
                    16 * 1024,
                    new HashSet<TransactionId> { retainedTransactionId }));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void InterruptedBranchDeletionCompletesDeterministicallyOnReopen()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedNthStorageFaultInjector(
                StorageFaultPoint.AfterBranchMetadataFlush,
                hitNumber: 1);
            Guid branchId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                var branch = database.CreateBranch("delete-me");
                branchId = branch.BranchId;
                branch.Put([1], [1]);
                branch.Dispose();

                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.DeleteBranch(branchId));
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.Empty(reopened.ListBranches());
            Assert.Throws<ChronicleDB.BranchNotFoundException>(() => reopened.OpenBranch(branchId));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void AppendBranchCommittedPut(
        WalLog wal,
        BranchId branchId,
        HistoryId historyId,
        CommitSequence sequence,
        byte[] key,
        byte[] value,
        TransactionId? transactionIdOverride = null)
    {
        var transactionId = transactionIdOverride ?? TransactionId.New();
        wal.Append(
            WalRecordType.Begin,
            transactionId,
            BranchWalEnvelopeCodec.Encode(branchId, historyId, []));
        wal.Append(
            WalRecordType.Put,
            transactionId,
            BranchWalEnvelopeCodec.Encode(
                branchId,
                historyId,
                WalMutationCodec.EncodePut(new BinaryKey(key), value)));
        wal.Append(
            WalRecordType.Commit,
            transactionId,
            BranchWalEnvelopeCodec.Encode(
                branchId,
                historyId,
                WalCommitCodec.Encode(sequence, 0)));
    }

    private sealed class ArmedStorageFaultInjector(StorageFaultPoint target) : IStorageFaultInjector
    {
        private int _armed;
        private int _fired;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Hit(StorageFaultPoint point, ChronicleDB.Core.Identifiers.PageId pageId)
        {
            if (Volatile.Read(ref _armed) != 0
                && point == target
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new InvalidOperationException($"Injected storage fault at {point}.");
            }
        }
    }


    private sealed class ArmedNthStorageFaultInjector(StorageFaultPoint target, int hitNumber) : IStorageFaultInjector
    {
        private int _armed;
        private int _hits;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Hit(StorageFaultPoint point, ChronicleDB.Core.Identifiers.PageId pageId)
        {
            if (Volatile.Read(ref _armed) == 0 || point != target)
            {
                return;
            }

            if (Interlocked.Increment(ref _hits) == hitNumber)
            {
                throw new InvalidOperationException($"Injected storage fault at {point} hit {hitNumber}.");
            }
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "chronicle-branch-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
