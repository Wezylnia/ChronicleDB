using ChronicleDB;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ResearchAncestryRoutingIntegrationTests
{
    [Fact]
    public void StableRoutingUsesLogicalBoundaryAndRebuildsAfterReopen()
    {
        using var directory = new StorageTestDirectory();
        Guid leafBranchId;

        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([0x11], [0x21]);
            database.Put([0x12], [0x22]);
            using var parent = database.CreateBranch("route-parent");
            Assert.True(parent.Delete([0x12]));
            using var leaf = parent.CreateBranch("route-leaf");
            leafBranchId = leaf.BranchId;

            database.SetResearchAncestryRoutingEnabled(leaf.BranchId, enabled: true);
            Assert.True(leaf.TryGet([0x11], out var inherited));
            Assert.Equal([0x21], inherited);
            Assert.False(leaf.TryGet([0x12], out _));
            Assert.False(leaf.TryGet([0x13], out _));

            var built = database.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
            Assert.True(built.Enabled);
            Assert.Equal(3, built.EntryCount);
            Assert.Equal(3, built.Misses);
            Assert.Equal(3, built.Builds);
            Assert.Equal(0, built.Hits);

            Assert.True(leaf.TryGet([0x11], out inherited));
            Assert.Equal([0x21], inherited);
            Assert.False(leaf.TryGet([0x12], out _));
            Assert.False(leaf.TryGet([0x13], out _));
            var hit = database.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
            Assert.Equal(3, hit.Hits);

            // The child base is immutable: later parent/main changes must not alter the
            // cached inherited route or its resolved value.
            database.Put([0x11], [0x31]);
            parent.Put([0x11], [0x32]);
            Assert.True(leaf.TryGet([0x11], out inherited));
            Assert.Equal([0x21], inherited);

            // A local overlay always wins before ancestry routing is consulted.
            leaf.Put([0x11], [0x41]);
            var beforeLocalRead = database.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
            Assert.True(leaf.TryGet([0x11], out var local));
            Assert.Equal([0x41], local);
            var afterLocalRead = database.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
            Assert.Equal(beforeLocalRead.Hits, afterLocalRead.Hits);

            _ = database.RunCompaction();
            Assert.False(leaf.TryGet([0x12], out _));
            Assert.False(leaf.TryGet([0x13], out _));
            Assert.Equal(0, database.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId).Invalidations);
        }

        using (var reopened = ChronicleDatabase.Open(directory.Path))
        using (var leaf = reopened.OpenBranch(leafBranchId))
        {
            var disabled = reopened.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
            Assert.False(disabled.Enabled);
            Assert.Equal(0, disabled.EntryCount);

            reopened.SetResearchAncestryRoutingEnabled(leaf.BranchId, enabled: true);
            Assert.True(leaf.TryGet([0x11], out var local));
            Assert.Equal([0x41], local);
            Assert.False(leaf.TryGet([0x12], out _));
            Assert.False(leaf.TryGet([0x13], out _));
            var rebuilt = reopened.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
            Assert.Equal(2, rebuilt.EntryCount);
            Assert.Equal(2, rebuilt.Builds);
        }
    }
}
