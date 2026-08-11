using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class DeterministicResearchWorkloadGeneratorTests
{
    [Fact]
    public void SameFamilyAndSeedProduceIdenticalOperations()
    {
        var first = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S7MixedAdversarialSoak, 42, 100);
        var second = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S7MixedAdversarialSoak, 42, 100);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeedsChangeTheGeneratedWorkload()
    {
        var first = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S4WideIndependentHistories, 42, 20);
        var second = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S4WideIndependentHistories, 43, 20);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EveryFamilyProducesValidatedOperations()
    {
        foreach (var family in Enum.GetValues<ResearchWorkloadFamily>())
        {
            var operations = DeterministicResearchWorkloadGenerator.Generate(family, 7, 64);

            Assert.Equal(64, operations.Count);
            Assert.Equal(Enumerable.Range(0, 64), operations.Select(operation => operation.Step));
            foreach (var operation in operations)
            {
                operation.Validate();
            }
        }
    }

    [Fact]
    public void ScenarioShapesExposeTheirTargetedStressors()
    {
        var deep = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S3DeepInheritance, 1, 40);
        var recovery = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S5RecoveryHeavy, 1, 40);
        var erasure = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S6ErasureConflict, 1, 10);

        Assert.Contains(deep, operation => operation.Kind == ResearchWorkloadOperationKind.CreateBranch && operation.ParentHistorySlot >= 0);
        Assert.Contains(recovery, operation => operation.Kind == ResearchWorkloadOperationKind.Crash);
        Assert.Contains(recovery, operation => operation.Kind == ResearchWorkloadOperationKind.Recover && operation.RequestedHistory);
        Assert.Contains(erasure, operation => operation.Kind == ResearchWorkloadOperationKind.Delete);
        Assert.Contains(erasure, operation => operation.Kind == ResearchWorkloadOperationKind.CreateSnapshot);
    }

    [Fact]
    public void NegativeOperationCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S0Control, 1, -1));
    }
}
