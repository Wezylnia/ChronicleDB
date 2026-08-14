using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class CapabilityCandidateGrammarTests
{
    [Fact]
    public void GrammarUsesCapabilitiesAndContainsNoIssueIdentifiers()
    {
        BranchCapabilityProfile profile = BranchCapabilityProfile.Create(
            "test",
            supportsHistoricalFork: true,
            supportsRestart: true,
            supportsDelete: true,
            equivalentObservers: ["primary", "alternate"]);

        IReadOnlyList<CapabilityCandidate> candidates = CapabilityCandidateGrammar.Generate(profile);

        Assert.Contains(candidates, candidate => candidate.Id == "drop-recreate-same-name");
        Assert.Contains(candidates, candidate => candidate.Id == "alternate-observer");
        Assert.Contains(candidates, candidate => candidate.Id == "restart");
        Assert.Contains(candidates, candidate => candidate.Id == "delete-branch");
        Assert.DoesNotContain(candidates, candidate => candidate.Id.Contains("27092", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, candidate => candidate.Id.Contains("26120", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedCapabilitiesDoNotCreateSpecializedCandidates()
    {
        IReadOnlyList<CapabilityCandidate> candidates = CapabilityCandidateGrammar.Generate(
            BranchCapabilityProfile.Create("test"));

        Assert.DoesNotContain(candidates, candidate => candidate.OperationClass == TraceOperationClass.BranchSpecificHistory);
        Assert.DoesNotContain(candidates, candidate => candidate.OperationClass == TraceOperationClass.BranchSpecificLifecycle);
        Assert.DoesNotContain(candidates, candidate => candidate.OperationClass == TraceOperationClass.Restart);
        Assert.DoesNotContain(candidates, candidate => candidate.OperationClass == TraceOperationClass.ObserverRead);
    }

    [Fact]
    public void UniformOrderingIsASeededPermutation()
    {
        BranchCapabilityProfile profile = BranchCapabilityProfile.Create(
            "test",
            supportsHistoricalFork: true,
            supportsRestart: true,
            supportsDelete: true,
            equivalentObservers: ["primary", "alternate"]);

        IReadOnlyList<CapabilityCandidate> expected = CapabilityCandidateGrammar.Generate(profile);
        IReadOnlyList<CapabilityCandidate> first = CapabilityCandidateGrammar.UniformOrdering(profile, 17);
        IReadOnlyList<CapabilityCandidate> second = CapabilityCandidateGrammar.UniformOrdering(profile, 17);

        Assert.Equal(expected.Count, first.Count);
        Assert.Equal(first.Select(static candidate => candidate.Id), second.Select(static candidate => candidate.Id));
        Assert.Equal(
            expected.Select(static candidate => candidate.Id).OrderBy(static id => id),
            first.Select(static candidate => candidate.Id).OrderBy(static id => id));
    }

    [Fact]
    public void GuidedOrderingPrioritizesSemanticRiskButRetainsEveryCandidate()
    {
        BranchCapabilityProfile profile = BranchCapabilityProfile.Create(
            "test",
            supportsHistoricalFork: true,
            supportsRestart: true,
            supportsDelete: true,
            equivalentObservers: ["primary", "alternate"]);

        IReadOnlyList<CapabilityCandidate> guided = CapabilityCandidateGrammar.GuidedOrdering(profile, 1);
        int firstOrdinary = guided.ToList().FindIndex(static candidate =>
            candidate.SemanticClasses == CandidateSemanticClass.Ordinary);

        Assert.True(firstOrdinary > 0);
        Assert.Equal(
            CapabilityCandidateGrammar.Generate(profile).Select(static candidate => candidate.Id).OrderBy(static id => id),
            guided.Select(static candidate => candidate.Id).OrderBy(static id => id));
    }

    [Fact]
    public void FingerprintIsStableForTheSameCapabilityProfile()
    {
        BranchCapabilityProfile first = BranchCapabilityProfile.Create(
            "test",
            supportsHistoricalFork: true,
            supportsRestart: true,
            supportsDelete: true,
            equivalentObservers: ["primary", "alternate"]);
        BranchCapabilityProfile second = BranchCapabilityProfile.Create(
            "test",
            supportsHistoricalFork: true,
            supportsRestart: true,
            supportsDelete: true,
            equivalentObservers: ["alternate", "primary"]);

        Assert.Equal(
            CapabilityCandidateGrammar.Fingerprint(first),
            CapabilityCandidateGrammar.Fingerprint(second));
    }
}
