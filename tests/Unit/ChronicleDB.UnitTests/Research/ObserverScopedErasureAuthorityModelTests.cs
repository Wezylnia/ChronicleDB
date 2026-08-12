using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ObserverScopedErasureAuthorityModelTests
{
    [Fact]
    public void SafeProtocolPreservesObserverAndAcknowledgementInvariants()
    {
        var result = ObserverScopedErasureAuthorityModel.Explore(maxDepth: 10);

        Assert.True(result.IsSafe);
        Assert.Empty(result.Violations);
        Assert.True(result.UniqueStateCount > 30);
        Assert.True(result.TransitionCount > result.UniqueStateCount);
    }

    [Theory]
    [InlineData(
        ObserverScopedErasureMutant.RecoverIgnoresDurableAuthority,
        "RevokedTargetNotServed")]
    [InlineData(
        ObserverScopedErasureMutant.PrematureAcknowledgement,
        "Acknowledge")]
    [InlineData(
        ObserverScopedErasureMutant.RewriteBeforeAuthority,
        "Rewrite")]
    [InlineData(
        ObserverScopedErasureMutant.AuthorityRevokesUnrelatedObservation,
        "NonTargetObservationStable")]
    [InlineData(
        ObserverScopedErasureMutant.AuthorityRevokesNonBlockingTargetObservation,
        "NonBlockingTargetObservationStable")]
    [InlineData(
        ObserverScopedErasureMutant.PublishGenericRedactionScope,
        "ExactObserverScope")]
    public void CheckerRejectsDeliberateProtocolMutants(
        ObserverScopedErasureMutant mutant,
        string expectedInvariantFragment)
    {
        var result = ObserverScopedErasureAuthorityModel.Explore(maxDepth: 10, mutant);

        Assert.False(result.IsSafe);
        Assert.Contains(
            result.Violations,
            violation => violation.Invariant.Contains(expectedInvariantFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void DurableAuthorityCanMaskTargetBeforePhysicalClosureWithoutAcknowledgingErasure()
    {
        var state = ObserverScopedErasureState.Initial with
        {
            ForceAuthorized = true,
            AuthorityDurable = true,
            RuntimeAuthorityLoaded = true,
        };

        Assert.False(state.RevokedTargetValueCanBeServed);
        Assert.True(state.AnyTargetRepresentationRemains);
        Assert.False(state.Acknowledged);
    }
}
