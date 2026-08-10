using ChronicleDB.Core.Sequences;

namespace ChronicleDB.UnitTests.Core;

public sealed class CommitSequenceTests
{
    [Fact]
    public void NextAdvancesLogicalHistoryByOne()
    {
        var sequence = new CommitSequence(41);

        Assert.Equal(new CommitSequence(42), sequence.Next());
    }

    [Fact]
    public void NextRejectsOverflow()
    {
        var sequence = new CommitSequence(ulong.MaxValue);

        Assert.Throws<OverflowException>(() => sequence.Next());
    }
}
