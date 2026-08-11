using ChronicleDB.Maintenance;

namespace ChronicleDB.CorrectnessTests;

public sealed class V10ReleaseGateTests
{
    [Fact]
    public void BranchingRetentionMaintenanceAndRestartPreserveAllSurvivingHistories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-v10-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Guid branchAId;
            Guid branchBId;
            Guid childId;
            Guid branchSnapshotId;

            using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                database.Put([1], [10]);
                database.Put([2], [20]);
                database.Put([3], [30]);
                using var source = database.CreateSnapshot("shared-base");
                var sourceId = source.SnapshotId;

                using var branchA = source.CreateBranch("A");
                using var branchB = source.CreateBranch("B");
                branchAId = branchA.BranchId;
                branchBId = branchB.BranchId;

                // Parent continues independently after the branch point.
                database.Put([1], [11]);
                database.Put([4], [40]);

                branchA.Put([1], [101]);
                branchA.Put([5], [50]);
                using var branchSnapshot = branchA.CreateSnapshot("A-stable");
                branchSnapshotId = branchSnapshot.Info.SnapshotId;
                branchA.Put([1], [102]);

                branchB.Put([1], [201]);
                Assert.True(branchB.Delete([2]));
                branchB.Put([6], [60]);

                using var child = branchA.CreateBranch("A-child");
                childId = child.BranchId;
                Assert.True(child.TryGet([1], out var childInherited));
                Assert.Equal(new byte[] { 102 }, childInherited);
                child.Put([7], [70]);

                // The named source root is no longer required once independent
                // branch-base roots exist. The still-open source handle remains a
                // process-local observer and must also survive maintenance.
                database.DeleteSnapshot(sourceId);

                for (byte value = 12; value < 32; value++)
                {
                    database.Put([1], [value]);
                    branchA.Put([5], [value]);
                    branchB.Put([6], [value]);
                }

                _ = database.RunGarbageCollection(new GarbageCollectionOptions
                {
                    RetainRecentCommits = 1,
                    IncludeBranches = true,
                });
                _ = database.RunCompaction(new CompactionOptions
                {
                    MaxHistoriesPerPass = 8,
                    MinimumReclaimableBytes = 1,
                    MaxBytesRewrittenPerPass = long.MaxValue,
                });

                Assert.True(source.TryGet([1], out var sourceValue));
                Assert.Equal(new byte[] { 10 }, sourceValue);
                Assert.True(source.TryGet([2], out var sourceKey2));
                Assert.Equal(new byte[] { 20 }, sourceKey2);

                Assert.True(database.TryGet([1], out var mainValue));
                Assert.Equal(new byte[] { 31 }, mainValue);
                Assert.True(database.TryGet([4], out var mainOnly));
                Assert.Equal(new byte[] { 40 }, mainOnly);

                Assert.True(branchA.TryGet([1], out var aValue));
                Assert.Equal(new byte[] { 102 }, aValue);
                Assert.True(branchA.TryGet([2], out var aInherited));
                Assert.Equal(new byte[] { 20 }, aInherited);
                Assert.False(branchA.TryGet([4], out _));

                Assert.True(branchSnapshot.TryGet([1], out var aHistorical));
                Assert.Equal(new byte[] { 101 }, aHistorical);
                Assert.True(branchSnapshot.TryGet([2], out var aSnapshotInherited));
                Assert.Equal(new byte[] { 20 }, aSnapshotInherited);

                Assert.True(branchB.TryGet([1], out var bValue));
                Assert.Equal(new byte[] { 201 }, bValue);
                Assert.False(branchB.TryGet([2], out _));
                Assert.False(branchB.TryGet([4], out _));

                Assert.True(child.TryGet([1], out var childStable));
                Assert.Equal(new byte[] { 102 }, childStable);
                Assert.True(child.TryGet([7], out var childLocal));
                Assert.Equal(new byte[] { 70 }, childLocal);

                var topology = database.GetHistoryTopologyDiagnostics();
                Assert.Equal(database.DatabaseId, topology.Main.HistoryId);
                Assert.Equal(3, topology.Branches.Count);
                Assert.Contains(topology.Branches, item => item.BranchId == branchAId && item.ParentHistoryId == database.DatabaseId);
                Assert.Contains(topology.Branches, item => item.BranchId == branchBId && item.ParentHistoryId == database.DatabaseId);
                Assert.Contains(topology.Branches, item => item.BranchId == childId && item.Depth == 2);
                Assert.Contains(topology.RetentionRoots, item => item.Kind == "BranchBase" && item.ProtectedHistoryId == database.DatabaseId);
                Assert.DoesNotContain(database.ListSnapshots(), item => item.SnapshotId == sourceId);
            }

            using (var reopened = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                Assert.Empty(reopened.ListSnapshots());
                Assert.True(reopened.TryGet([1], out var main));
                Assert.Equal(new byte[] { 31 }, main);

                using (var branchA = reopened.OpenBranch(branchAId))
                {
                    Assert.True(branchA.TryGet([1], out var a));
                    Assert.Equal(new byte[] { 102 }, a);
                    Assert.True(branchA.TryGet([2], out var inherited));
                    Assert.Equal(new byte[] { 20 }, inherited);
                    using var snapshot = branchA.OpenSnapshot(branchSnapshotId);
                    Assert.True(snapshot.TryGet([1], out var stable));
                    Assert.Equal(new byte[] { 101 }, stable);
                }

                using (var branchB = reopened.OpenBranch(branchBId))
                {
                    Assert.True(branchB.TryGet([1], out var b));
                    Assert.Equal(new byte[] { 201 }, b);
                    Assert.False(branchB.TryGet([2], out _));
                }

                using (var child = reopened.OpenBranch(childId))
                {
                    Assert.True(child.TryGet([1], out var inherited));
                    Assert.Equal(new byte[] { 102 }, inherited);
                    Assert.True(child.TryGet([7], out var local));
                    Assert.Equal(new byte[] { 70 }, local);
                }

                reopened.DeleteBranch(branchBId);
                var gc = reopened.RunGarbageCollection(new GarbageCollectionOptions
                {
                    RetainRecentCommits = 1,
                    IncludeBranches = true,
                });
                Assert.DoesNotContain(reopened.ListBranches(), item => item.BranchId == branchBId);
                Assert.True(gc.DeletedBranchDirectoriesReclaimed + gc.DeletedBranchDirectoriesPending >= 1);
            }

            using var finalOpen = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.DoesNotContain(finalOpen.ListBranches(), item => item.BranchId == branchBId);
            using var finalA = finalOpen.OpenBranch(branchAId);
            using var finalSnapshot = finalA.OpenSnapshot(branchSnapshotId);
            Assert.True(finalSnapshot.TryGet([1], out var finalHistorical));
            Assert.Equal(new byte[] { 101 }, finalHistorical);
            using var finalChild = finalOpen.OpenBranch(childId);
            Assert.True(finalChild.TryGet([7], out var finalChildLocal));
            Assert.Equal(new byte[] { 70 }, finalChildLocal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public void TopologyDiagnosticsExposePerHistoryWalStorageAndRetentionOwnership()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-v10-topology-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            database.Put([1], [10]);
            using var mainSnapshot = database.CreateSnapshot("main-root");
            using var branch = mainSnapshot.CreateBranch("diagnostic-branch");
            branch.Put([1], [20]);
            using var branchSnapshot = branch.CreateSnapshot("branch-root");
            using var transaction = branch.BeginTransaction();
            using var historical = branch.OpenHistoricalView(branch.CurrentSequence);

            var diagnostics = database.GetDiagnostics();
            var topology = database.GetHistoryTopologyDiagnostics();

            Assert.Equal(1, diagnostics.BranchCount);
            Assert.True(diagnostics.BranchLocalWalBytes > 0);
            Assert.True(diagnostics.HistoryRootMetadataBytes > 0);
            Assert.Equal(database.DatabaseId, topology.Main.HistoryId);
            Assert.True(topology.Main.SnapshotCount >= 1);

            var branchDiagnostics = Assert.Single(topology.Branches);
            Assert.Equal(branch.BranchId, branchDiagnostics.BranchId);
            Assert.Equal(database.DatabaseId, branchDiagnostics.ParentHistoryId);
            Assert.True(branchDiagnostics.WalFileBytes > 0);
            Assert.True(branchDiagnostics.DataFileBytes > 0);
            Assert.Equal(1, branchDiagnostics.ActiveTransactionCount);
            Assert.True(branchDiagnostics.OpenHistoricalHandleCount >= 2);
            Assert.True(branchDiagnostics.OpenRetentionBoundaryCount >= 3);

            Assert.Contains(topology.RetentionRoots, root =>
                root.Kind == "PersistentSnapshot"
                && root.ProtectedHistoryId == database.DatabaseId
                && root.Boundary == mainSnapshot.Sequence);
            Assert.Contains(topology.RetentionRoots, root =>
                root.Kind == "BranchBase"
                && root.ProtectedHistoryId == database.DatabaseId
                && root.OwnerHistoryId == branch.HistoryId);
            Assert.Contains(topology.RetentionRoots, root =>
                root.Kind == "PersistentSnapshot"
                && root.ProtectedHistoryId == branch.HistoryId
                && root.Boundary == branchSnapshot.Info.Sequence);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
