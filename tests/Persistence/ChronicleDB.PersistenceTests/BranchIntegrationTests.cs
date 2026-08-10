using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class BranchIntegrationTests
{
    [Fact]
    public void BranchSharesFixedParentBaseAndIsolatesLocalWritesAndTombstones()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        database.Put([2], [20]);

        using var branchA = database.CreateBranch("A");
        using var branchB = database.CreateBranch("B");
        Assert.True(branchA.TryGet([1], out var inheritedA));
        Assert.Equal(new byte[] { 10 }, inheritedA);
        Assert.True(branchB.TryGet([1], out var inheritedB));
        Assert.Equal(new byte[] { 10 }, inheritedB);

        database.Put([1], [99]);
        Assert.True(branchA.TryGet([1], out inheritedA));
        Assert.Equal(new byte[] { 10 }, inheritedA);

        branchA.Put([1], [11]);
        Assert.True(branchA.TryGet([1], out var localA));
        Assert.Equal(new byte[] { 11 }, localA);
        Assert.True(branchB.TryGet([1], out inheritedB));
        Assert.Equal(new byte[] { 10 }, inheritedB);
        Assert.True(database.TryGet([1], out var main));
        Assert.Equal(new byte[] { 99 }, main);

        Assert.True(branchA.Delete([2]));
        Assert.False(branchA.TryGet([2], out _));
        Assert.True(branchB.TryGet([2], out var sibling));
        Assert.Equal(new byte[] { 20 }, sibling);
        Assert.True(database.TryGet([2], out var mainTwo));
        Assert.Equal(new byte[] { 20 }, mainTwo);
    }

    [Fact]
    public void BranchCanStartFromAnExplicitRetainedMainSequence()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        var baseSequence = database.CurrentCommitSequence.Value;
        database.Put([1], [20]);

        using var branch = database.CreateBranch("historical-main", baseSequence);
        Assert.True(branch.TryGet([1], out var inherited));
        Assert.Equal(new byte[] { 10 }, inherited);

        database.Put([1], [30]);
        Assert.True(branch.TryGet([1], out inherited));
        Assert.Equal(new byte[] { 10 }, inherited);
    }

    [Fact]
    public void BranchFromSnapshotSurvivesSourceSnapshotDeletionAndParentEvolution()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        using var snapshot = database.CreateSnapshot("base");
        database.Put([1], [20]);

        using var branch = snapshot.CreateBranch("from-base");
        database.DeleteSnapshot(snapshot.SnapshotId);
        database.Put([1], [30]);

        Assert.True(branch.TryGet([1], out var value));
        Assert.Equal(new byte[] { 10 }, value);
        Assert.Equal((ulong)0, branch.CurrentSequence);
        Assert.Single(database.ListBranches());
    }


    [Fact]
    public void BranchTransactionReadsOwnWritesAndAbortLeavesHistoryUnchanged()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        using var branch = database.CreateBranch("read-own-writes");

        using (var transaction = branch.BeginTransaction())
        {
            Assert.True(transaction.TryGet([1], out var inherited));
            Assert.Equal(new byte[] { 10 }, inherited);
            transaction.Put([1], [20]);
            Assert.True(transaction.TryGet([1], out var local));
            Assert.Equal(new byte[] { 20 }, local);
            transaction.Delete([1]);
            Assert.False(transaction.TryGet([1], out _));
            transaction.Abort();
        }

        Assert.Equal((ulong)0, branch.CurrentSequence);
        Assert.True(branch.TryGet([1], out var afterAbort));
        Assert.Equal(new byte[] { 10 }, afterAbort);
    }

    [Fact]
    public void BranchHistoricalReadsCombineLocalHistoryWithImmutableParentBase()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [5]);
        database.Put([2], [7]);
        using var branch = database.CreateBranch("history");

        branch.Put([1], [6]);
        branch.Delete([2]);
        branch.Put([1], [8]);

        using var atZero = branch.OpenHistoricalView(0);
        Assert.True(atZero.TryGet([1], out var zeroOne));
        Assert.Equal(new byte[] { 5 }, zeroOne);
        Assert.True(atZero.TryGet([2], out var zeroTwo));
        Assert.Equal(new byte[] { 7 }, zeroTwo);

        using var atOne = branch.OpenHistoricalView(1);
        Assert.True(atOne.TryGet([1], out var one));
        Assert.Equal(new byte[] { 6 }, one);
        Assert.True(atOne.TryGet([2], out _));

        using var atTwo = branch.OpenHistoricalView(2);
        Assert.False(atTwo.TryGet([2], out _));
        Assert.True(atTwo.TryGet([1], out var twoOne));
        Assert.Equal(new byte[] { 6 }, twoOne);

        using var atThree = branch.OpenHistoricalView(3);
        Assert.True(atThree.TryGet([1], out var three));
        Assert.Equal(new byte[] { 8 }, three);
        Assert.Throws<ChronicleDB.BranchHistoricalStateUnavailableException>(
            () => branch.OpenHistoricalView(4));
    }


    [Fact]
    public void BranchLocalLargeValueUsesOverflowStorageAndSurvivesRestart()
    {
        using var directory = new StorageTestDirectory();
        var value = Enumerable.Range(0, 128 * 1024)
            .Select(index => checked((byte)(index % 251)))
            .ToArray();
        Guid branchId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            using var branch = database.CreateBranch("large-value");
            branchId = branch.BranchId;
            branch.Put([7], value);
            Assert.True(branch.TryGet([7], out var actual));
            Assert.Equal(value, actual);
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var recovered = reopened.OpenBranch(branchId);
        Assert.True(recovered.TryGet([7], out var recoveredValue));
        Assert.Equal(value, recoveredValue);
    }

    [Fact]
    public void BranchSnapshotIsStableAndReopensAfterDatabaseRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid branchId;
        Guid snapshotId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            using var branch = database.CreateBranch("persisted");
            branch.Put([1], [11]);
            using var snapshot = branch.CreateSnapshot("branch-s1");
            branch.Put([1], [12]);
            database.Put([1], [99]);

            Assert.True(snapshot.TryGet([1], out var stable));
            Assert.Equal(new byte[] { 11 }, stable);
            branchId = branch.BranchId;
            snapshotId = snapshot.Info.SnapshotId;
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var recoveredBranch = reopened.OpenBranch(branchId);
        Assert.Equal((ulong)2, recoveredBranch.CurrentSequence);
        Assert.True(recoveredBranch.TryGet([1], out var current));
        Assert.Equal(new byte[] { 12 }, current);
        using var recoveredSnapshot = recoveredBranch.OpenSnapshot(snapshotId);
        Assert.True(recoveredSnapshot.TryGet([1], out var historical));
        Assert.Equal(new byte[] { 11 }, historical);
    }

    [Fact]
    public async Task BranchSnapshotIsolationAllowsDisjointWritersAndRejectsSameKeyRace()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([9], [9]);
        using var branch = database.CreateBranch("concurrent");

        using var reader = branch.BeginTransaction();
        var t1 = branch.BeginTransaction();
        var t2 = branch.BeginTransaction();
        t1.Put([1], [1]);
        t2.Put([2], [2]);
        await Task.WhenAll(Task.Run(t1.Commit), Task.Run(t2.Commit));
        Assert.False(reader.TryGet([1], out _));
        Assert.False(reader.TryGet([2], out _));
        Assert.True(reader.TryGet([9], out var inherited));
        Assert.Equal(new byte[] { 9 }, inherited);
        reader.Abort();
        t1.Dispose();
        t2.Dispose();

        var c1 = branch.BeginTransaction();
        var c2 = branch.BeginTransaction();
        c1.Put([3], [31]);
        c2.Put([3], [32]);
        var outcomes = await Task.WhenAll(
            Task.Run(() => TryCommit(c1)),
            Task.Run(() => TryCommit(c2)));
        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Equal(1, outcomes.Count(value => !value));
        c1.Dispose();
        c2.Dispose();
    }

    [Fact]
    public async Task SameLogicalKeyCanCommitIndependentlyInMainAndSiblingBranches()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        using var branchA = database.CreateBranch("same-key-a");
        using var branchB = database.CreateBranch("same-key-b");

        using var main = database.BeginTransaction();
        using var a = branchA.BeginTransaction();
        using var b = branchB.BeginTransaction();
        main.Put([1], [10]);
        a.Put([1], [20]);
        b.Put([1], [30]);

        await Task.WhenAll(
            Task.Run(main.Commit),
            Task.Run(a.Commit),
            Task.Run(b.Commit));

        Assert.True(database.TryGet([1], out var mainValue));
        Assert.Equal(new byte[] { 10 }, mainValue);
        Assert.True(branchA.TryGet([1], out var aValue));
        Assert.Equal(new byte[] { 20 }, aValue);
        Assert.True(branchB.TryGet([1], out var bValue));
        Assert.Equal(new byte[] { 30 }, bValue);
    }

    [Fact]
    public void NestedBranchFreezesParentBranchAtSelectedLocalSequence()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        database.Put([1], [1]);
        using var parent = database.CreateBranch("parent");
        parent.Put([1], [2]);
        using var child = parent.CreateBranch("child");
        parent.Put([1], [3]);
        database.Put([1], [4]);

        Assert.True(child.TryGet([1], out var inherited));
        Assert.Equal(new byte[] { 2 }, inherited);
        child.Put([1], [5]);
        Assert.True(child.TryGet([1], out var local));
        Assert.Equal(new byte[] { 5 }, local);
        Assert.True(parent.TryGet([1], out var parentValue));
        Assert.Equal(new byte[] { 3 }, parentValue);
        Assert.True(database.TryGet([1], out var main));
        Assert.Equal(new byte[] { 4 }, main);
    }


    [Fact]
    public void NestedBranchDepthLimitIsExplicitlyEnforced()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        var handles = new List<ChronicleDB.ChronicleBranch>();
        try
        {
            var current = database.CreateBranch("depth-1");
            handles.Add(current);
            for (var depth = 2; depth <= ChronicleDB.ChronicleBranch.MaximumDepth; depth++)
            {
                current = current.CreateBranch($"depth-{depth}");
                handles.Add(current);
            }

            Assert.Throws<InvalidOperationException>(() => current.CreateBranch("too-deep"));
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    [Fact]
    public void BranchCreationIsMetadataOrientedAndDoesNotCopyParentDataFile()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        for (var i = 0; i < 200; i++)
        {
            database.Put(BitConverter.GetBytes(i), new byte[512]);
        }

        using var branch = database.CreateBranch("cheap-create");
        var branchData = Path.Combine(
            directory.Path,
            "branches",
            branch.BranchId.ToString("N"),
            ChronicleDB.Storage.Files.PersistentKeyValueStore.DataFileName);
        Assert.True(File.Exists(branchData));
        Assert.Equal(0, new FileInfo(branchData).Length);
        Assert.True(branch.TryGet(BitConverter.GetBytes(199), out var inherited));
        Assert.Equal(512, inherited.Length);
    }

    private static bool TryCommit(ChronicleDB.ChronicleTransaction transaction)
    {
        try
        {
            transaction.Commit();
            return true;
        }
        catch (ChronicleDB.TransactionConflictException)
        {
            return false;
        }
    }
}
