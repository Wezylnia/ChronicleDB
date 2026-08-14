using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class ExternalSqlAdapterTests
{
    [Fact]
    public void MatrixOneScalarOutputParserPreservesCreationAndContinuationEvidence()
    {
        MatrixOneAutoIncrementObservation observation = MatrixOneAutoIncrementOutputParser.Parse(
            "4.0.1\n1,2,3\n1,2,3\n10001\n4\n10001\n4\n10002\n5\n");

        Assert.Equal("4.0.1", observation.ServerVersion);
        Assert.Equal("1,2,3", observation.CloneRowsAtCreation);
        Assert.Equal("10001", observation.CloneNextAtCreation);
        Assert.Equal("4", observation.ReferenceNextAtCreation);
        Assert.Equal("10001", observation.CloneInsertedId);
        Assert.Equal("4", observation.ReferenceInsertedId);
    }

    [Fact]
    public void MatrixOneScalarOutputParserRejectsIncompleteEvidence()
    {
        Assert.Throws<ExternalAdapterException>(() =>
            MatrixOneAutoIncrementOutputParser.Parse("1,2,3\n1,2,3\n4\n"));
    }

    [Fact]
    public void MatrixOneHistoricalIdentityParserPreservesObjectGenerations()
    {
        MatrixOneHistoricalIdentityObservation observation = MatrixOneHistoricalIdentityOutputParser.Parse(
            "8.0.30-MatrixOne-v4.1.4\n287580\n287581\n1:snapshot-row\n287581\n287581\n");

        Assert.Equal("287580", observation.SnapshotParentId);
        Assert.Equal("287581", observation.CurrentParentId);
        Assert.Equal("1:snapshot-row", observation.ChildRow);
        Assert.Equal("287581", observation.BranchParentId);
        Assert.Equal("287581", observation.ProtectionSnapshotObjectId);
    }

    [Fact]
    public void HistoricalIdentityRelationCanFailWhileGenericReadBaselinePasses()
    {
        var declared = new BranchBoundary("object:287580", 0);
        var wrong = new BranchBoundary("object:287581", 0);
        var branch = CanonicalState.Create(
            [new KeyValuePair<string, string>("parent_t", "1:snapshot-row")],
            "parent_t(id,val)",
            "ordinary-table-visible-state",
            componentBoundaries: new Dictionary<string, BranchBoundary>(StringComparer.Ordinal)
            {
                ["data"] = declared,
                ["metadata"] = wrong,
                ["dependencies"] = wrong,
            });
        var reference = CanonicalState.Create(
            [new KeyValuePair<string, string>("parent_t", "1:snapshot-row")],
            "parent_t(id,val)",
            "ordinary-table-visible-state");
        var scenario = new BranchScenario(
            "identity-negative-control",
            BranchCapabilityProfile.Create(
                "MatrixOne",
                supportsHistoricalFork: true,
                sourceBoundaryComponents: ["data", "metadata", "dependencies"]),
            declared,
            branch,
            reference,
            [new TraceFrame("read-child", new ObserverObservation(OutcomeClass.Success, branch), new ObserverObservation(OutcomeClass.Success, reference), OperationClass: TraceOperationClass.GenericRead)],
            CreationEvidence: CreationEvidenceKind.Values | CreationEvidenceKind.Schema);

        ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
        BaselineResult b2 = Assert.Single(report.Baselines, static result => result.BaselineId == "B2.generic-state-differential");
        RelationResult boundary = Assert.Single(report.Relations, static result => result.RelationId == "BC.temporal-boundary");

        Assert.Equal(BaselineStatus.Pass, b2.Status);
        Assert.Equal(RelationStatus.Fail, boundary.Status);
    }

    [Fact]
    public void SlateDbObserverParserPreservesObserverCountsAndFailureEvidence()
    {
        SlateDbObserverObservation observation = SlateDbObserverOutputParser.Parse(
            "version=0.14.1\ntotal=128\ndb=128\nreader=0\nreader_error=external SST not found\n");

        Assert.Equal("0.14.1", observation.Version);
        Assert.Equal(128, observation.TotalKeys);
        Assert.Equal(128, observation.DbReadableKeys);
        Assert.Equal(0, observation.DbReaderReadableKeys);
        Assert.Equal("external SST not found", observation.ReaderError);
    }
}
