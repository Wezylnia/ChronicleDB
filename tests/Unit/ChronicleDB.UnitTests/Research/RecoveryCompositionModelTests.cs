using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class RecoveryCompositionModelTests
{
    [Fact]
    public void SafeTransitionSystemPreservesBoundedCompositionInvariants()
    {
        var result = RecoveryCompositionModel.Explore(maxDepth: 7);

        Assert.True(result.IsSafe);
        Assert.Empty(result.Violations);
        Assert.True(result.UniqueStateCount > 10);
        Assert.True(result.TransitionCount > result.UniqueStateCount);
    }

    [Theory]
    [InlineData(RecoveryCompositionMutant.ChildBaseAheadOfDurableParent, "ChildBase")]
    [InlineData(RecoveryCompositionMutant.RecoverMainBeyondDurablePrefix, "Recover(Main)")]
    [InlineData(RecoveryCompositionMutant.RecoverChildBeyondDurablePrefix, "Recover(Child)")]
    public void CheckerRejectsNonVacuousCompositionMutants(
        RecoveryCompositionMutant mutant,
        string expectedInvariantFragment)
    {
        var result = RecoveryCompositionModel.Explore(maxDepth: 7, mutant);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, violation => violation.Invariant.Contains(expectedInvariantFragment, StringComparison.Ordinal));
    }
}
