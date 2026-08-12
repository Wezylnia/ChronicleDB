using ChronicleDB.Maintenance;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ShadowAwareGarbageCollectionTests
{
    [Fact]
    public void ReclaimsOnlyShadowedParentPredecessorAndSurvivesRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid branchId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            database.Put([2], [11]);
            using var branch = database.CreateBranch("shadowed-k");
            branchId = branch.BranchId;
            branch.Put([1], [20]);
            database.Put([1], [30]);
            database.Put([2], [31]);

            var result = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 0,
            });

            Assert.True(result.ReclaimedVersions > 0);
            Assert.Equal(1, result.ShadowReleasedPayloadBytes);
            Assert.True(result.ShadowAwareReclamationRatio > 1d);
            Assert.True(branch.TryGet([1], out var local));
            Assert.Equal(new byte[] { 20 }, local);
            Assert.True(branch.TryGet([2], out var inherited));
            Assert.Equal(new byte[] { 11 }, inherited);
            Assert.True(database.TryGet([1], out var mainK));
            Assert.Equal(new byte[] { 30 }, mainK);
            Assert.True(database.TryGet([2], out var mainX));
            Assert.Equal(new byte[] { 31 }, mainX);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        using var recovered = reopened.OpenBranch(branchId);
        Assert.True(recovered.TryGet([1], out var recoveredLocal));
        Assert.Equal(new byte[] { 20 }, recoveredLocal);
        Assert.True(recovered.TryGet([2], out var recoveredInherited));
        Assert.Equal(new byte[] { 11 }, recoveredInherited);
        Assert.True(reopened.TryGet([1], out var recoveredMainK));
        Assert.Equal(new byte[] { 30 }, recoveredMainK);
        Assert.True(reopened.TryGet([2], out var recoveredMainX));
        Assert.Equal(new byte[] { 31 }, recoveredMainX);
    }

    [Fact]
    public void PreShadowPersistentSnapshotPreventsParentReleaseAcrossRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid branchId;
        Guid snapshotId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            using var branch = database.CreateBranch("pre-shadow-snapshot");
            branchId = branch.BranchId;
            using var snapshot = branch.CreateSnapshot("before-local-write");
            snapshotId = snapshot.Info.SnapshotId;
            branch.Put([1], [20]);
            database.Put([1], [30]);

            var result = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 0,
            });

            Assert.Equal(0, result.ShadowReleasedPayloadBytes);
            Assert.True(snapshot.TryGet([1], out var historical));
            Assert.Equal(new byte[] { 10 }, historical);
            Assert.True(branch.TryGet([1], out var current));
            Assert.Equal(new byte[] { 20 }, current);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        using var recovered = reopened.OpenBranch(branchId);
        using var recoveredSnapshot = recovered.OpenSnapshot(snapshotId);
        Assert.True(recoveredSnapshot.TryGet([1], out var historicalAfterRestart));
        Assert.Equal(new byte[] { 10 }, historicalAfterRestart);
        Assert.True(recovered.TryGet([1], out var currentAfterRestart));
        Assert.Equal(new byte[] { 20 }, currentAfterRestart);
    }

    [Fact]
    public void PartialFloorAdvanceReleasesOnlyAfterShadowCommitBecomesFloorVisible()
    {
        using var beforeDirectory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(beforeDirectory.Path))
        {
            database.Put([1], [10]);
            using var branch = database.CreateBranch("retain-shadow-before-floor");
            branch.Put([1], [20]);
            branch.Put([2], [21]);
            branch.Put([3], [22]);
            database.Put([1], [30]);
            database.Put([4], [40]);
            database.Put([5], [50]);

            var result = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 3,
            });

            Assert.Equal(0, result.ShadowReleasedPayloadBytes);
            Assert.True(branch.TryGet([1], out var current));
            Assert.Equal(new byte[] { 20 }, current);
        }

        using var afterDirectory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(afterDirectory.Path))
        {
            database.Put([1], [10]);
            using var branch = database.CreateBranch("retain-shadow-at-floor");
            branch.Put([1], [20]);
            branch.Put([2], [21]);
            branch.Put([3], [22]);
            database.Put([1], [30]);
            database.Put([4], [40]);
            database.Put([5], [50]);

            var result = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 2,
            });

            Assert.Equal(1, result.ShadowReleasedPayloadBytes);
            Assert.True(branch.TryGet([1], out var current));
            Assert.Equal(new byte[] { 20 }, current);
        }
    }

    [Fact]
    public void ActivePreShadowHistoricalViewPreventsParentReleaseUntilDisposed()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put([1], [10]);
        using var branch = database.CreateBranch("active-pre-shadow");
        using var historical = branch.OpenHistoricalView(branch.CurrentSequence);
        branch.Put([1], [20]);
        database.Put([1], [30]);

        var protectedResult = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 0,
        });

        Assert.Equal(0, protectedResult.ShadowReleasedPayloadBytes);
        Assert.True(historical.TryGet([1], out var oldValue));
        Assert.Equal(new byte[] { 10 }, oldValue);
        Assert.True(branch.TryGet([1], out var current));
        Assert.Equal(new byte[] { 20 }, current);

        historical.Dispose();
        var afterDispose = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 0,
        });
        Assert.Equal(1, afterDispose.ShadowReleasedPayloadBytes);
        Assert.True(branch.TryGet([1], out var afterRelease));
        Assert.Equal(new byte[] { 20 }, afterRelease);
    }

    [Fact]
    public void NestedProjectionPublishesDescendantsBeforeAncestorsAndSurvivesRestart()
    {
        using var directory = new StorageTestDirectory();
        Guid aBranchId;
        Guid bBranchId;
        Guid aHistoryId;
        Guid bHistoryId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [10]);
            using var a = database.CreateBranch("a");
            aBranchId = a.BranchId;
            aHistoryId = a.HistoryId;
            a.Put([1], [20]);
            using var b = a.CreateBranch("b");
            bBranchId = b.BranchId;
            bHistoryId = b.HistoryId;
            b.Put([1], [30]);
            a.Put([1], [40]);
            database.Put([1], [50]);

            var result = database.RunShadowAwareGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 0,
            });

            Assert.True(result.ShadowReleasedPayloadBytes >= 2);
            var bIndex = result.PublishedHistoryOrder.IndexOf(bHistoryId);
            var aIndex = result.PublishedHistoryOrder.IndexOf(aHistoryId);
            Assert.True(bIndex >= 0);
            Assert.True(aIndex > bIndex);
            Assert.Equal(result.PublishedHistoryOrder.Count - 1, aIndex + 1);
            Assert.True(b.TryGet([1], out var bValue));
            Assert.Equal(new byte[] { 30 }, bValue);
            Assert.True(a.TryGet([1], out var aValue));
            Assert.Equal(new byte[] { 40 }, aValue);
            Assert.True(database.TryGet([1], out var mainValue));
            Assert.Equal(new byte[] { 50 }, mainValue);
        }

        using var reopened = ChronicleDatabase.Open(directory.Path);
        using var recoveredA = reopened.OpenBranch(aBranchId);
        using var recoveredB = reopened.OpenBranch(bBranchId);
        Assert.True(recoveredA.TryGet([1], out var aAfterRestart));
        Assert.Equal(new byte[] { 40 }, aAfterRestart);
        Assert.True(recoveredB.TryGet([1], out var bAfterRestart));
        Assert.Equal(new byte[] { 30 }, bAfterRestart);
        Assert.True(reopened.TryGet([1], out var mainAfterRestart));
        Assert.Equal(new byte[] { 50 }, mainAfterRestart);
    }
}

internal static class ReadOnlyGuidListExtensions
{
    internal static int IndexOf(this IReadOnlyList<Guid> values, Guid value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }
}
