using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class ExternalSqlAdapterTests
{
    [Fact]
    public void MatrixOneScalarOutputParserPreservesCreationAndContinuationEvidence()
    {
        MatrixOneAutoIncrementObservation observation = MatrixOneAutoIncrementOutputParser.Parse(
            "1,2,3\n1,2,3\n10001\n4\n10001\n4\n10002\n5\n");

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
}
