using ChronicleDB.Maintenance;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.PersistenceTests;

public sealed class MaintenanceV09Tests
{
    [Fact]
    public void GarbageCollectionPreservesPersistentRootsAndOpenHistoricalHandles()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        using var named = database.CreateSnapshot("old-main");
        using var openHandle = database.OpenHistoricalView(named.Sequence);
        using var branch = named.CreateBranch("old-branch");

        for (var value = 20; value < 40; value++)
        {
            database.Put([1], [checked((byte)value)]);
        }

        database.DeleteSnapshot(named.SnapshotId);
        var result = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 2,
        });

        Assert.True(result.VersionsReclaimed > 0);
        Assert.True(openHandle.TryGet([1], out var historical));
        Assert.Equal(new byte[] { 10 }, historical);
        Assert.True(branch.TryGet([1], out var inherited));
        Assert.Equal(new byte[] { 10 }, inherited);
        Assert.Throws<ChronicleDB.HistoricalStateUnavailableException>(
            () => database.OpenHistoricalView(2));
    }

    [Fact]
    public void EmptyBinaryKeySurvivesHistoryCheckpointGarbageCollectionAndRestartInMainAndBranch()
    {
        using var directory = new StorageTestDirectory();
        Guid branchId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([], [1]);
            database.Put([], [2]);
            using var branch = database.CreateBranch("empty-key-gc");
            branchId = branch.BranchId;
            branch.Put([], [3]);
            branch.Put([], [4]);

            _ = database.RunGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 1,
                IncludeBranches = true,
            });

            Assert.True(database.TryGet([], out var main));
            Assert.Equal(new byte[] { 2 }, main);
            Assert.True(branch.TryGet([], out var local));
            Assert.Equal(new byte[] { 4 }, local);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var recovered = reopened.OpenBranch(branchId);
        Assert.True(reopened.TryGet([], out var mainAfterRestart));
        Assert.Equal(new byte[] { 2 }, mainAfterRestart);
        Assert.True(recovered.TryGet([], out var branchAfterRestart));
        Assert.Equal(new byte[] { 4 }, branchAfterRestart);
    }

    [Fact]
    public void MainCheckpointOutsideConfiguredLogicalLimitsIsRejectedBeforeRecoveryRedo()
    {
        using var directory = new StorageTestDirectory();
        var options = new StorageOptions
        {
            MaxKeySize = 4,
            MaxValueSize = 16,
        };
        Guid databaseId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path, options))
        {
            database.Put([1], [1]);
            database.Put([1], [2]);
            _ = database.RunGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 1,
            });
            databaseId = database.DatabaseId;
        }

        var historyId = new ChronicleDB.Core.Identifiers.HistoryId(databaseId);
        var checkpoint = ChronicleDB.Storage.History.PersistentHistoryCheckpoint.TryLoad(
            directory.Path,
            databaseId,
            historyId);
        Assert.NotNull(checkpoint);
        var anchor = checkpoint!.Versions
            .OrderByDescending(version => version.CommitSequence.Value)
            .First();
        var versions = checkpoint.Versions
            .Append(new ChronicleDB.Storage.History.HistoryCheckpointVersion(
                anchor.TransactionId,
                anchor.CommitSequence,
                new ChronicleDB.Core.Keys.BinaryKey(new byte[5]),
                false,
                new byte[] { 9 }))
            .ToArray();
        _ = ChronicleDB.Storage.History.PersistentHistoryCheckpoint.Publish(
            directory.Path,
            checkpoint with { Versions = versions });

        Assert.Throws<StorageCorruptionException>(() =>
            ChronicleDB.ChronicleDatabase.Open(directory.Path, options));
    }

    [Fact]
    public void BranchCheckpointOutsideConfiguredLogicalLimitsIsRejectedBeforeDerivedStateRecovery()
    {
        using var directory = new StorageTestDirectory();
        var options = new StorageOptions
        {
            MaxKeySize = 4,
            MaxValueSize = 16,
        };
        Guid branchId;
        Guid historyId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path, options))
        {
            database.Put([1], [1]);
            using var branch = database.CreateBranch("checkpoint-limit");
            branchId = branch.BranchId;
            historyId = branch.HistoryId;
            branch.Put([1], [2]);
            branch.Put([1], [3]);
            _ = database.RunGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 1,
                IncludeBranches = true,
            });
        }

        var branchDirectory = Path.Combine(
            directory.Path,
            "branches",
            branchId.ToString("N"));
        Guid localStorageId;
        using (var localStore = ChronicleDB.Storage.Files.PersistentKeyValueStore.Open(branchDirectory))
        {
            localStorageId = localStore.DatabaseId;
        }

        var typedHistoryId = new ChronicleDB.Core.Identifiers.HistoryId(historyId);
        var checkpoint = ChronicleDB.Storage.History.PersistentHistoryCheckpoint.TryLoad(
            branchDirectory,
            localStorageId,
            typedHistoryId);
        Assert.NotNull(checkpoint);
        var anchor = checkpoint!.Versions
            .OrderByDescending(version => version.CommitSequence.Value)
            .First();
        var versions = checkpoint.Versions
            .Append(new ChronicleDB.Storage.History.HistoryCheckpointVersion(
                anchor.TransactionId,
                anchor.CommitSequence,
                new ChronicleDB.Core.Keys.BinaryKey(new byte[5]),
                false,
                new byte[] { 9 }))
            .ToArray();
        _ = ChronicleDB.Storage.History.PersistentHistoryCheckpoint.Publish(
            branchDirectory,
            checkpoint with { Versions = versions });

        Assert.Throws<StorageCorruptionException>(() =>
            ChronicleDB.ChronicleDatabase.Open(directory.Path, options));
    }

    [Fact]
    public void BranchSnapshotAndBranchHistorySurviveGarbageCollectionAndRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid branchId;
        Guid snapshotId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([9], [9]);
            using var branch = database.CreateBranch("branch-gc");
            branch.Put([1], [1]);
            using var snapshot = branch.CreateSnapshot("branch-old");
            snapshotId = snapshot.Info.SnapshotId;
            branchId = branch.BranchId;
            for (byte value = 2; value < 25; value++)
            {
                branch.Put([1], [value]);
            }

            var result = database.RunGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 2,
            });
            Assert.True(result.VersionsReclaimed > 0);
            Assert.True(snapshot.TryGet([1], out var historical));
            Assert.Equal(new byte[] { 1 }, historical);
            Assert.True(branch.TryGet([1], out var current));
            Assert.Equal(new byte[] { 24 }, current);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var recovered = reopened.OpenBranch(branchId);
        using var recoveredSnapshot = recovered.OpenSnapshot(snapshotId);
        Assert.True(recoveredSnapshot.TryGet([1], out var historicalAfterRestart));
        Assert.Equal(new byte[] { 1 }, historicalAfterRestart);
        Assert.True(recovered.TryGet([1], out var currentAfterRestart));
        Assert.Equal(new byte[] { 24 }, currentAfterRestart);
    }

    [Fact]
    public void CompactionShrinksAppendOnlyMainStorageAndPreservesHistoricalStateAcrossRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid snapshotId;
        long before;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], Enumerable.Repeat((byte)1, 8 * 1024).ToArray());
            using var snapshot = database.CreateSnapshot("pre-compact");
            snapshotId = snapshot.SnapshotId;
            for (byte value = 2; value < 35; value++)
            {
                database.Put([1], Enumerable.Repeat(value, 8 * 1024).ToArray());
            }

            before = new FileInfo(Path.Combine(
                directory.Path,
                ChronicleDB.Storage.Files.PersistentKeyValueStore.DataFileName)).Length;
            _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 2 });
            var result = database.RunCompaction(new CompactionOptions
            {
                MaxHistoriesPerPass = 4,
                MinimumReclaimableBytes = 1,
            });

            Assert.True(result.HistoriesCompacted >= 1);
            Assert.True(result.BytesReclaimed > 0);
            var after = new FileInfo(Path.Combine(
                directory.Path,
                ChronicleDB.Storage.Files.PersistentKeyValueStore.DataFileName)).Length;
            Assert.True(after < before);
            Assert.True(snapshot.TryGet([1], out var old));
            Assert.All(old, value => Assert.Equal((byte)1, value));
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var recoveredSnapshot = reopened.OpenSnapshot(snapshotId);
        Assert.True(recoveredSnapshot.TryGet([1], out var historical));
        Assert.All(historical, value => Assert.Equal((byte)1, value));
        Assert.True(reopened.TryGet([1], out var current));
        Assert.All(current, value => Assert.Equal((byte)34, value));
    }

    [Fact]
    public void GarbageCollectionCompactsLifecycleJournalsForBoundedCreateDeleteWorkloads()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);

        for (var index = 0; index < 20; index++)
        {
            var snapshot = database.CreateSnapshot($"temp-s-{index}");
            var snapshotId = snapshot.SnapshotId;
            snapshot.Dispose();
            database.DeleteSnapshot(snapshotId);

            var branch = database.CreateBranch($"temp-b-{index}");
            var branchId = branch.BranchId;
            branch.Dispose();
            database.DeleteBranch(branchId);
        }

        var snapshotPath = Path.Combine(
            directory.Path,
            ChronicleDB.Storage.Snapshots.PersistentSnapshotStore.FileName);
        var rootPath = Path.Combine(
            directory.Path,
            ChronicleDB.Storage.HistoryRoots.PersistentHistoryRootStore.FileName);
        var branchPath = Path.Combine(
            directory.Path,
            ChronicleDB.Storage.Branches.PersistentBranchMetadataStore.FileName);
        var before = new FileInfo(snapshotPath).Length
            + new FileInfo(rootPath).Length
            + new FileInfo(branchPath).Length;

        _ = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 1024,
        });

        var after = new FileInfo(snapshotPath).Length
            + new FileInfo(rootPath).Length
            + new FileInfo(branchPath).Length;
        Assert.True(after < before);
        Assert.Empty(database.ListSnapshots());
        Assert.Empty(database.ListBranches());
    }
    [Fact]
    public void PersistentSnapshotBelowGenericFloorCanStillCreateIndependentBranch()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        var snapshot = database.CreateSnapshot("branch-source");
        var snapshotId = snapshot.SnapshotId;
        snapshot.Dispose();

        for (byte value = 11; value < 35; value++)
        {
            database.Put([1], [value]);
        }

        _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 2 });
        Assert.True(database.HistoricalRetentionFloor > 1);

        using var branch = database.CreateBranchFromSnapshot(snapshotId, "from-retained-root");
        Assert.True(branch.TryGet([1], out var inherited));
        Assert.Equal(new byte[] { 10 }, inherited);

        database.DeleteSnapshot(snapshotId);
        Assert.True(branch.TryGet([1], out inherited));
        Assert.Equal(new byte[] { 10 }, inherited);
    }


    [Fact]
    public void DeletedPersistentRootShrinksProjectionWithoutAdvancingGenericFloor()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        var snapshot = database.CreateSnapshot("old-root");
        var snapshotId = snapshot.SnapshotId;
        snapshot.Dispose();

        for (byte value = 2; value <= 16; value++)
        {
            database.Put([1], [value]);
        }

        var first = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 2,
        });
        var floor = database.HistoricalRetentionFloor;
        var beforeDelete = database.GetDiagnostics().VersionCount;
        Assert.True(first.HistoriesProcessed >= 1);

        database.DeleteSnapshot(snapshotId);
        var second = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 2,
        });

        Assert.Equal(floor, database.HistoricalRetentionFloor);
        Assert.True(second.HistoriesProcessed >= 1);
        Assert.True(second.VersionsReclaimed > 0);
        Assert.True(database.GetDiagnostics().VersionCount < beforeDelete);
        Assert.Throws<ChronicleDB.HistoricalStateUnavailableException>(
            () => database.OpenHistoricalView(1));
        Assert.True(database.TryGet([1], out var current));
        Assert.Equal(new byte[] { 16 }, current);
    }

    [Fact]
    public async Task HistoricalHandleOpenCannotRacePastGarbageCollectionFloorPublication()
    {
        using var directory = new StorageTestDirectory();
        using var reachedCheckpoint = new ManualResetEventSlim(false);
        using var releaseCheckpoint = new ManualResetEventSlim(false);
        var injector = new BlockingCheckpointFaultInjector(reachedCheckpoint, releaseCheckpoint);
        using var database = ChronicleDB.ChronicleDatabase.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });

        for (byte value = 1; value <= 12; value++)
        {
            database.Put([1], [value]);
        }

        var gcTask = Task.Run(() => database.RunGarbageCollection(
            new GarbageCollectionOptions { RetainRecentCommits = 1 }));
        Assert.True(reachedCheckpoint.Wait(TimeSpan.FromSeconds(5)));

        var openTask = Task.Run(() => database.OpenHistoricalView(2));
        await Task.Delay(100);
        Assert.False(openTask.IsCompleted);

        releaseCheckpoint.Set();
        _ = await gcTask;
        await Assert.ThrowsAsync<ChronicleDB.HistoricalStateUnavailableException>(async () =>
        {
            using var _ = await openTask;
        });
    }


    [Fact]
    public async Task TransactionBeginCannotRacePastGarbageCollectionFloorPublication()
    {
        using var directory = new StorageTestDirectory();
        using var reachedCheckpoint = new ManualResetEventSlim(false);
        using var releaseCheckpoint = new ManualResetEventSlim(false);
        var injector = new BlockingCheckpointFaultInjector(reachedCheckpoint, releaseCheckpoint);
        using var database = ChronicleDB.ChronicleDatabase.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });

        for (byte value = 1; value <= 12; value++)
        {
            database.Put([1], [value]);
        }

        var gcTask = Task.Run(() => database.RunGarbageCollection(
            new GarbageCollectionOptions { RetainRecentCommits = 1 }));
        Assert.True(reachedCheckpoint.Wait(TimeSpan.FromSeconds(5)));

        var beginTask = Task.Run(() => database.BeginTransaction());
        await Task.Delay(100);
        Assert.False(beginTask.IsCompleted);

        releaseCheckpoint.Set();
        _ = await gcTask;
        using var transaction = await beginTask;

        Assert.True(transaction.StartSequence >= database.HistoricalRetentionFloor);
        Assert.True(transaction.TryGet([1], out var current));
        Assert.Equal(new byte[] { 12 }, current);
    }

    [Fact]
    public void BranchCanCommitAfterLifecycleJournalCompaction()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        using var branch = database.CreateBranch("post-gc-commit");
        branch.Put([2], [2]);

        _ = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 1024,
        });

        branch.Put([2], [3]);
        Assert.True(branch.TryGet([2], out var value));
        Assert.Equal(new byte[] { 3 }, value);
    }

    [Fact]
    public void AlreadyCompactedPhysicalStateIsNotRewrittenAgain()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [7]);

        var dataPath = Path.Combine(
            directory.Path,
            ChronicleDB.Storage.Files.PersistentKeyValueStore.DataFileName);
        var before = new FileInfo(dataPath).Length;
        var result = database.RunCompaction(new CompactionOptions
        {
            MinimumReclaimableBytes = 1,
            MaxHistoriesPerPass = 4,
        });

        Assert.Equal(0, result.HistoriesCompacted);
        Assert.Equal(0, result.BytesRewritten);
        Assert.Equal(before, new FileInfo(dataPath).Length);
    }

    [Fact]
    public void CompactionRewriteBudgetIsStrictAndDoesNotPartiallyPublishOversizedCandidate()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        for (byte value = 1; value <= 12; value++)
        {
            database.Put([1], Enumerable.Repeat(value, 4 * 1024).ToArray());
        }

        var dataPath = Path.Combine(
            directory.Path,
            ChronicleDB.Storage.Files.PersistentKeyValueStore.DataFileName);
        var before = new FileInfo(dataPath).Length;
        var result = database.RunCompaction(new CompactionOptions
        {
            MinimumReclaimableBytes = 1,
            MaxHistoriesPerPass = 1,
            MaxBytesRewrittenPerPass = 1,
        });

        Assert.Equal(0, result.HistoriesCompacted);
        Assert.Equal(0, result.BytesRewritten);
        Assert.Equal(before, new FileInfo(dataPath).Length);
        Assert.True(database.TryGet([1], out var current));
        Assert.All(current, value => Assert.Equal((byte)12, value));
    }

    private sealed class BlockingCheckpointFaultInjector(
        ManualResetEventSlim reached,
        ManualResetEventSlim release) : IStorageFaultInjector
    {
        private int _blocked;

        public void Hit(StorageFaultPoint point, ChronicleDB.Core.Identifiers.PageId pageId)
        {
            if (point == StorageFaultPoint.BeforeHistoryCheckpointWrite
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                reached.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Timed out waiting to release the GC checkpoint barrier.");
                }
            }
        }
    }

}
