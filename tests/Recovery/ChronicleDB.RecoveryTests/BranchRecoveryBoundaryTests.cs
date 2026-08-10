using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.RecoveryTests;

public sealed class BranchRecoveryBoundaryTests
{
    [Fact]
    public void FailedLocalAppendBeforeCommitMetadataIsDiscardedOnReopen()
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
            Assert.Equal((ulong)0, recovered.CurrentSequence);
            Assert.True(recovered.TryGet([1], out var inherited));
            Assert.Equal(new byte[] { 10 }, inherited);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CorruptionInsidePublishedBranchPrefixIsRejectedRatherThanTruncated()
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
                ChronicleDB.Storage.Files.PersistentKeyValueStore.DataFileName);
            var bytes = File.ReadAllBytes(branchDataPath);
            Assert.NotEmpty(bytes);
            bytes[Math.Min(100, bytes.Length - 1)] ^= 0x5A;
            File.WriteAllBytes(branchDataPath, bytes);

            Assert.Throws<StorageCorruptionException>(() => ChronicleDB.ChronicleDatabase.Open(directory));
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
