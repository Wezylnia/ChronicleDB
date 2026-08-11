using ChronicleDB.Maintenance;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.RecoveryTests;

public sealed class MaintenanceRecoveryTests
{
    [Theory]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointHeaderWrite)]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointRecordWrite)]
    [InlineData(StorageFaultPoint.AfterHistoryCheckpointOutputFlush)]
    [InlineData(StorageFaultPoint.BeforeHistoryWalReset)]
    [InlineData(StorageFaultPoint.AfterHistoryWalReset)]
    public void MainGcCheckpointWalRotationFaultReopensEquivalentHistory(StorageFaultPoint point)
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedStorageFaultInjector(point);
            Guid snapshotId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], [1]);
                using var snapshot = database.CreateSnapshot("gc-crash-root");
                snapshotId = snapshot.SnapshotId;
                for (byte value = 2; value < 12; value++)
                {
                    database.Put([1], [value]);
                }

                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.RunGarbageCollection(
                    new GarbageCollectionOptions { RetainRecentCommits = 1 }));
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.True(reopened.TryGet([1], out var current));
            Assert.Equal(new byte[] { 11 }, current);
            using var recoveredMainSnapshot = reopened.OpenSnapshot(snapshotId);
            Assert.True(recoveredMainSnapshot.TryGet([1], out var historical));
            Assert.Equal(new byte[] { 1 }, historical);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void BranchGcWalRotationFaultReopensEquivalentLocalAndInheritedHistory()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedStorageFaultInjector(StorageFaultPoint.AfterHistoryWalReset);
            Guid branchId;
            Guid snapshotId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([9], [9]);
                using var branch = database.CreateBranch("branch-gc-fault");
                branchId = branch.BranchId;
                branch.Put([1], [1]);
                using var snapshot = branch.CreateSnapshot("branch-root");
                snapshotId = snapshot.Info.SnapshotId;
                for (byte value = 2; value < 9; value++)
                {
                    branch.Put([1], [value]);
                }

                // Main has only one commit, so retain=1 leaves Main's floor unchanged.
                // The first checkpoint reset fault therefore occurs in the branch history.
                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.RunGarbageCollection(
                    new GarbageCollectionOptions { RetainRecentCommits = 1 }));
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            using var branchHandle = reopened.OpenBranch(branchId);
            Assert.True(branchHandle.TryGet([1], out var current));
            Assert.Equal(new byte[] { 8 }, current);
            Assert.True(branchHandle.TryGet([9], out var inherited));
            Assert.Equal(new byte[] { 9 }, inherited);
            using var branchSnapshot = branchHandle.OpenSnapshot(snapshotId);
            Assert.True(branchSnapshot.TryGet([1], out var historical));
            Assert.Equal(new byte[] { 1 }, historical);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CrashEquivalentFaultAfterCompactionPublicationReopensValidState()
    {
        var directory = NewDirectory();
        try
        {
            var injector = new ArmedStorageFaultInjector(StorageFaultPoint.AfterCompactionPublish);
            Guid snapshotId;
            using (var database = ChronicleDB.ChronicleDatabase.Open(
                       directory,
                       new StorageOptions { FaultInjector = injector }))
            {
                database.Put([1], Enumerable.Repeat((byte)1, 4096).ToArray());
                using var snapshot = database.CreateSnapshot("before-compact-fault");
                snapshotId = snapshot.SnapshotId;
                for (byte value = 2; value < 15; value++)
                {
                    database.Put([1], Enumerable.Repeat(value, 4096).ToArray());
                }

                injector.Arm();
                Assert.Throws<InvalidOperationException>(() => database.RunCompaction(
                    new CompactionOptions { MaxHistoriesPerPass = 1 }));
                Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.True(reopened.TryGet([1], out var current));
            Assert.All(current, value => Assert.Equal((byte)14, value));
            using var recoveredSnapshot = reopened.OpenSnapshot(snapshotId);
            Assert.True(recoveredSnapshot.TryGet([1], out var historical));
            Assert.All(historical, value => Assert.Equal((byte)1, value));
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
                throw new InvalidOperationException($"Injected maintenance fault at {point}.");
            }
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chronicle-v09-recovery-{Guid.NewGuid():N}");
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
