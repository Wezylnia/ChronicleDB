using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchWorkloadSerializerTests
{
    [Fact]
    public void CanonicalSerializationAndHashAreStable()
    {
        var first = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S2OverlappingRoots, 17, 12);
        var second = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S2OverlappingRoots, 17, 12);

        Assert.Equal(
            ResearchWorkloadSerializer.SerializeCanonical(first),
            ResearchWorkloadSerializer.SerializeCanonical(second));
        Assert.Equal(
            ResearchWorkloadSerializer.ComputeCanonicalSha256(first),
            ResearchWorkloadSerializer.ComputeCanonicalSha256(second));
    }

    [Fact]
    public void DifferentOperationOrderIsRejected()
    {
        var operations = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 1, 2)
            .Reverse()
            .ToArray();

        Assert.Throws<ArgumentException>(() => ResearchWorkloadSerializer.SerializeCanonical(operations));
    }

    [Fact]
    public void GeneratorFormatVersionIsBoundIntoCanonicalDocument()
    {
        var operations = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S1OldThinBranch, 1, 1);

        var serialized = ResearchWorkloadSerializer.SerializeCanonical(operations);

        Assert.Contains("\"generatorFormatVersion\":1", serialized, StringComparison.Ordinal);
    }
}
