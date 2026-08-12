using ChronicleDB.Maintenance;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.RecoveryTests;

public sealed class ShadowAwareGarbageCollectionRecoveryTests
{
    [Theory]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointHeaderWrite)]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointRecordWrite)]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointOutputFlush)]
    [InlineData(StorageFaultPoint.BeforeHistoryWalReset)]
    [InlineData(StorageFaultPoint.AfterHistoryWalReset)]
    public void ParentPublicationFaultAfterChildAuthorityReopensEquivalentState(StorageFaultPoint point)
    {
        var directory = NewDirectory();
        try
        {
            // One child checkpoint is published first. The second occurrence is Main,
            // where the shadow-aware projection is allowed to drop K's old predecessor.
            var injector = new ArmedNthStorageFaultInjector(point, hitNumber: 2);
            Guid branchId;
            using (var database = ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [10]);
                database.Put([2], [11]);
                using var branch = database.CreateBranch("shadow-crash");
                branchId = branch.BranchId;
                branch.Put([1], [20]);
                database.Put([1], [30]);
                database.Put([2], [31]);

                injector.Arm();
                Assert.Throws<InvalidOperationException>(() =>
                    database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
                    {
                        RetainRecentCommits = 0,
                    }));
                Assert.Equal(DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDatabase.Open(directory);
            using var recoveredBranch = reopened.OpenBranch(branchId);
            Assert.True(recoveredBranch.TryGet([1], out var local));
            Assert.Equal(new byte[] { 20 }, local);
            Assert.True(recoveredBranch.TryGet([2], out var inherited));
            Assert.Equal(new byte[] { 11 }, inherited);
            Assert.True(reopened.TryGet([1], out var mainK));
            Assert.Equal(new byte[] { 30 }, mainK);
            Assert.True(reopened.TryGet([2], out var mainX));
            Assert.Equal(new byte[] { 31 }, mainX);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointOutputFlush)]
    [InlineData(StorageFaultPoint.BeforeHistoryWalReset)]
    [InlineData(StorageFaultPoint.AfterHistoryWalReset)]
    public void ChildAuthorityFaultNeverAllowsAncestorReclamationToRaceAhead(StorageFaultPoint point)
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedNthStorageFaultInjector(point, hitNumber: 1);
            Guid branchId;
            using (var database = ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [10]);
                using var branch = database.CreateBranch("child-first-fault");
                branchId = branch.BranchId;
                branch.Put([1], [20]);
                database.Put([1], [30]);

                injector.Arm();
                Assert.Throws<InvalidOperationException>(() =>
                    database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
                    {
                        RetainRecentCommits = 0,
                    }));
            }

            using var reopened = ChronicleDatabase.Open(directory);
            using var recoveredBranch = reopened.OpenBranch(branchId);
            Assert.True(recoveredBranch.TryGet([1], out var local));
            Assert.Equal(new byte[] { 20 }, local);
            Assert.True(reopened.TryGet([1], out var main));
            Assert.Equal(new byte[] { 30 }, main);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private sealed class ArmedNthStorageFaultInjector(StorageFaultPoint target, int hitNumber) : IStorageFaultInjector
    {
        private int _armed;
        private int _hits;
        private int _fired;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Hit(StorageFaultPoint point, ChronicleDB.Core.Identifiers.PageId pageId)
        {
            if (Volatile.Read(ref _armed) == 0 || point != target)
            {
                return;
            }

            var hit = Interlocked.Increment(ref _hits);
            if (hit == hitNumber && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new InvalidOperationException(
                    $"Injected shadow-aware GC fault at {point}, occurrence {hitNumber}.");
            }
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chronicle-shadow-gc-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
