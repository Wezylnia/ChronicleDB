using ChronicleDB.Maintenance;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class BranchLifecycleV08Tests
{
    [Fact]
    public void DeleteBranchRejectsOpenBranchHandle()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        using var branch = database.CreateBranch("open-handle");

        Assert.Throws<ChronicleDB.BranchInUseException>(() => database.DeleteBranch(branch.BranchId));
    }

    [Fact]
    public void DeleteBranchRejectsPersistentSnapshotAndChildDependency()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        var parent = database.CreateBranch("parent-delete");
        var parentId = parent.BranchId;
        using var snapshot = parent.CreateSnapshot("keep-parent");
        using var child = parent.CreateBranch("child-delete");
        parent.Dispose();

        Assert.Throws<ChronicleDB.BranchInUseException>(() => database.DeleteBranch(parentId));
    }

    [Fact]
    public void BranchCanBeDeletedAfterSnapshotAndChildDependenciesAreReleased()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        var parent = database.CreateBranch("releasable-parent");
        var parentId = parent.BranchId;
        using var snapshot = parent.CreateSnapshot("temporary-snapshot");
        var snapshotId = snapshot.Info.SnapshotId;
        var child = parent.CreateBranch("temporary-child");
        var childId = child.BranchId;

        child.Dispose();
        database.DeleteBranch(childId);
        parent.DeleteSnapshot(snapshotId);
        snapshot.Dispose();
        parent.Dispose();

        database.DeleteBranch(parentId);
        Assert.Empty(database.ListBranches());
    }

    [Fact]
    public void DeletedBranchIsNotReopenableAndGcReclaimsItsPrivateDirectory()
    {
        using var directory = new StorageTestDirectory();
        Guid branchId;
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory.Path))
        {
            var branch = database.CreateBranch("reclaim-delete");
            branchId = branch.BranchId;
            branch.Put([1], Enumerable.Repeat((byte)7, 32 * 1024).ToArray());
            branch.Dispose();

            database.DeleteBranch(branchId);
            Assert.Empty(database.ListBranches());
            var branchDirectory = Path.Combine(directory.Path, "branches", branchId.ToString("N"));
            Assert.True(Directory.Exists(branchDirectory));

            var result = database.RunGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 1024,
            });
            Assert.Equal(1, result.DeletedBranchDirectoriesReclaimed);
            Assert.False(Directory.Exists(branchDirectory));
        }

        using var reopened = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.Empty(reopened.ListBranches());
    }
}
