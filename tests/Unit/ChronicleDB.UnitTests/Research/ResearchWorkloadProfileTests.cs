using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchWorkloadProfileTests
{
    [Fact]
    public void ProfileCapturesBranchTopologyAndOperationKinds()
    {
        var operations = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S3DeepInheritance, 1, 20);

        var profile = ResearchWorkloadProfiler.Analyze(operations);

        Assert.Equal(20, profile.OperationCount);
        Assert.Equal(10, profile.BranchCount);
        Assert.Equal(10, profile.MaximumBranchDepth);
        Assert.Equal(1, profile.MaximumFanout);
        Assert.Equal(0, profile.SnapshotCount);
        Assert.Equal(0, profile.CrashCount);
        Assert.True(profile.MaximumValueSize > 0);
    }

    [Fact]
    public void RecoveryProfileCountsCrashAndRequestedRecoveryOperations()
    {
        var profile = ResearchWorkloadProfiler.Analyze(
            DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S5RecoveryHeavy, 1, 20));

        Assert.Equal(8, profile.BranchCount);
        Assert.True(profile.CrashCount > 0);
        Assert.True(profile.RequestedRecoveryCount > 0);
    }

    [Fact]
    public void ProfileRejectsOperationReferencingUnknownHistory()
    {
        var invalid = new ResearchWorkloadOperation(
            Step: 0,
            Kind: ResearchWorkloadOperationKind.Put,
            HistorySlot: 4,
            ParentHistorySlot: -1,
            KeyId: 1,
            ValueSize: 1,
            RequestedHistory: false);

        Assert.Throws<ArgumentException>(() => ResearchWorkloadProfiler.Analyze([invalid]));
    }
}
