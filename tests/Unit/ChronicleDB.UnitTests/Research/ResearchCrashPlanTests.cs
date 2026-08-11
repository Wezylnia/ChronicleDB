using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchCrashPlanTests
{
    [Fact]
    public void CrashPlanIsDeterministicForSameWorkloadAndSeed()
    {
        var workload = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S5RecoveryHeavy, 1, 32);

        var first = ResearchCrashPlanFactory.Create(workload, 99);
        var second = ResearchCrashPlanFactory.Create(workload, 99);

        Assert.Equal(
            ResearchCrashPlanSerializer.ComputeCanonicalSha256(first),
            ResearchCrashPlanSerializer.ComputeCanonicalSha256(second));
        Assert.Equal(first.Injections, second.Injections);
        Assert.NotEmpty(first.Injections);
    }

    [Fact]
    public void DifferentSeedsChangeFaultPointSelection()
    {
        var workload = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S5RecoveryHeavy, 1, 32);

        var first = ResearchCrashPlanFactory.Create(workload, 99);
        var second = ResearchCrashPlanFactory.Create(workload, 100);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EmptyCrashWorkloadProducesValidEmptyPlan()
    {
        var workload = DeterministicResearchWorkloadGenerator.Generate(ResearchWorkloadFamily.S1OldThinBranch, 1, 16);

        var plan = ResearchCrashPlanFactory.Create(workload, 1);

        Assert.Empty(plan.Injections);
        plan.Validate();
        Assert.Contains("\"injections\":[]", ResearchCrashPlanSerializer.SerializeCanonical(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void NonIncreasingInjectionStepsAreRejected()
    {
        var plan = new ResearchCrashPlan(
            1,
            1,
            [
                new ResearchCrashInjection(3, 1, "BeforeWalAppend"),
                new ResearchCrashInjection(3, 1, "AfterWalAppend"),
            ]);

        Assert.Throws<InvalidOperationException>(() => plan.Validate());
    }
}
