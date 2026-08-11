using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class RetentionAnalyzerTests
{
    [Fact]
    public void MarginalDebtSeparatesUniqueAndSharedVersions()
    {
        var shared = new RetentionVersion("v-shared", logicalPayloadBytes: 10, serializedBytes: 20);
        var onlyA = new RetentionVersion("v-a", logicalPayloadBytes: 30, serializedBytes: 40);
        var onlyB = new RetentionVersion("v-b", logicalPayloadBytes: 50, serializedBytes: 60);
        var context = new RetentionContext(
            globallyRequiredVersions: [],
            roots:
            [
                new RetentionRoot("A", [shared, onlyA]),
                new RetentionRoot("B", [shared, onlyB]),
            ]);

        var result = MarginalRetentionAnalyzer.Analyze(context, ["A"]);

        Assert.Equal(3, result.ProtectedVersionCount);
        Assert.Equal(2, result.ProtectedVersionCountAfterDrop);
        Assert.Equal(90, result.CurrentLivePayloadBytes);
        Assert.Equal(60, result.LivePayloadBytesAfterDrop);
        Assert.Equal(30, result.MarginalPayloadBytes);
        Assert.Equal(1, result.UniqueRequiredVersionCount);
        Assert.Equal(1, result.SharedRequiredVersionCount);
        Assert.Equal(30, result.UniqueProtectedPayloadBytes);
        Assert.Equal(10, result.SharedProtectedPayloadBytes);
    }

    [Fact]
    public void GlobalRequirementCannotBeDroppedByRootSet()
    {
        var global = new RetentionVersion("floor", logicalPayloadBytes: 7, serializedBytes: 9);
        var rootVersion = new RetentionVersion("root", logicalPayloadBytes: 11, serializedBytes: 13);
        var context = new RetentionContext(
            globallyRequiredVersions: [global],
            roots: [new RetentionRoot("A", [rootVersion])]);

        var result = MarginalRetentionAnalyzer.Analyze(context, ["A"]);

        Assert.Equal(18, result.CurrentLivePayloadBytes);
        Assert.Equal(7, result.LivePayloadBytesAfterDrop);
        Assert.Equal(11, result.MarginalPayloadBytes);
    }

    [Fact]
    public void ConflictingVersionMetadataIsRejected()
    {
        var context = new RetentionContext(
            globallyRequiredVersions: [new RetentionVersion("v", 10, 20)],
            roots: [new RetentionRoot("A", [new RetentionVersion("v", 11, 20)])]);

        Assert.Throws<ArgumentException>(() => MarginalRetentionAnalyzer.Analyze(context, ["A"]));
    }

    [Fact]
    public void UnknownOrEmptyRootSetIsRejected()
    {
        var context = new RetentionContext(
            globallyRequiredVersions: [],
            roots: [new RetentionRoot("A", [])]);

        Assert.Throws<ArgumentException>(() => MarginalRetentionAnalyzer.Analyze(context, []));
        Assert.Throws<ArgumentException>(() => MarginalRetentionAnalyzer.Analyze(context, ["missing"]));
    }

    [Fact]
    public void TombstonesCanCarrySerializedCostWithoutLogicalPayload()
    {
        var tombstone = new RetentionVersion("tombstone", logicalPayloadBytes: 0, serializedBytes: 8, isTombstone: true);
        var context = new RetentionContext([], [new RetentionRoot("A", [tombstone])]);

        var result = MarginalRetentionAnalyzer.Analyze(context, ["A"]);

        Assert.Equal(0, result.MarginalPayloadBytes);
        Assert.Equal(8, result.MarginalSerializedBytes);
    }
}
