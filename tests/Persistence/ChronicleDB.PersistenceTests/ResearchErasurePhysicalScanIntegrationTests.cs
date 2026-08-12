using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ResearchErasurePhysicalScanIntegrationTests
{
    [Fact]
    public void PhysicalClosureFindsSupersededOverflowRecordsAfterLogicalGc()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x51, 0x52, 0x53, 0x54 };
        var first = Enumerable.Repeat((byte)0xA1, 48 * 1024).ToArray();
        var second = Enumerable.Repeat((byte)0xB2, 48 * 1024).ToArray();

        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put(key, first);
        database.Put(key, second);

        _ = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 0,
            IncludeBranches = true,
        });

        var input = database.CaptureResearchErasureClosureInput(key);
        var analysis = ErasureClosureAnalyzer.Analyze(input, ErasureScope.Global);

        Assert.True(input.PhysicalRepresentationScanComplete);
        Assert.Empty(input.UnscannedPhysicalRepresentations);
        Assert.True(analysis.ClosureIsComplete);
        Assert.True(analysis.PhysicalDataRecordOccurrences > analysis.MvccVersionOccurrences);
        Assert.True(analysis.PhysicalDataRecordOccurrences >= 2);
        Assert.True(analysis.PhysicalOverflowChunkOccurrences >= 6);
        Assert.Contains(input.Representations, representation =>
            representation.Kind == ErasureRepresentationKind.PhysicalDataRecord
            && representation.Content == ErasureContentState.Value);
        Assert.Contains(input.Representations, representation =>
            representation.Kind == ErasureRepresentationKind.PhysicalOverflowChunk
            && representation.Content == ErasureContentState.Value);
    }

    [Fact]
    public void BranchPhysicalScanDecodesLogicalKeyInsideVersionEnvelope()
    {
        using var directory = new StorageTestDirectory();
        var key = new byte[] { 0x61, 0x62, 0x63 };
        var first = Enumerable.Repeat((byte)0xC3, 32 * 1024).ToArray();
        var second = Enumerable.Repeat((byte)0xD4, 32 * 1024).ToArray();

        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put([0x01], [0x02]);
        using var branch = database.CreateBranch("erasure-physical-branch");
        branch.Put(key, first);
        branch.Put(key, second);

        _ = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 0,
            IncludeBranches = true,
        });

        var input = database.CaptureResearchErasureClosureInput(key, branch.HistoryId);
        var analysis = ErasureClosureAnalyzer.Analyze(input, ErasureScope.Local);

        Assert.True(analysis.ClosureIsComplete);
        Assert.True(analysis.PhysicalDataRecordOccurrences > analysis.MvccVersionOccurrences);
        Assert.True(analysis.PhysicalOverflowChunkOccurrences > 0);
        Assert.All(
            input.Representations.Where(representation =>
                representation.Kind is ErasureRepresentationKind.PhysicalDataRecord
                    or ErasureRepresentationKind.PhysicalOverflowChunk),
            representation => Assert.Equal(branch.HistoryId, representation.OwnerHistoryId));
    }
}
