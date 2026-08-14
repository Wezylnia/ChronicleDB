using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class CapabilityBudgetCampaignTests
{
    [Fact]
    public void DefaultCalibrationUsesFrozenSeedsAndHasGuidedAdvantage()
    {
        IReadOnlyList<CapabilityBudgetReport> reports = CapabilityBudgetCampaign.ExecuteDefault();

        Assert.Equal([1, 7, 13, 29, 61, 127, 251, 509], CapabilityBudgetCampaign.DefaultSeeds);
        Assert.Equal(4, reports.Count);
        Assert.All(reports, report =>
        {
            Assert.Equal(CapabilityBudgetCampaign.DefaultSeeds, report.Seeds);
            Assert.True(report.GuidedHasAdvantageAtAnyBudget);
            Assert.Equal(report.CandidateCount, report.BudgetCurve.Count);
            Assert.Equal(report.CandidateSetFingerprint, CapabilityCandidateGrammar.Fingerprint(report.Profile));
        });
    }

    [Fact]
    public void FairBudgetKeepsCandidateSetConstantAcrossOrderings()
    {
        BranchCapabilityProfile profile = BranchCapabilityProfile.Create(
            "calibration",
            supportsHistoricalFork: true,
            supportsRestart: true,
            supportsDelete: true,
            equivalentObservers: ["primary", "alternate"]);
        IReadOnlyList<CapabilityCandidate> uniform = CapabilityCandidateGrammar.UniformOrdering(profile, 29);
        IReadOnlyList<CapabilityCandidate> guided = CapabilityCandidateGrammar.GuidedOrdering(
            profile,
            CandidateSemanticClass.DependencyAffecting,
            29);

        Assert.Equal(uniform.Count, guided.Count);
        Assert.Equal(
            uniform.Select(static candidate => candidate.Id).OrderBy(static id => id),
            guided.Select(static candidate => candidate.Id).OrderBy(static id => id));
    }

    [Fact]
    public void SameSeedProducesSameCurve()
    {
        BranchCapabilityProfile profile = BranchCapabilityProfile.Create(
            "calibration",
            supportsHistoricalFork: true,
            equivalentObservers: ["primary", "alternate"]);
        CapabilityBudgetReport first = CapabilityBudgetCampaign.Execute(
            "observer",
            profile,
            CandidateSemanticClass.ObserverAffecting,
            [11, 23, 47]);
        CapabilityBudgetReport second = CapabilityBudgetCampaign.Execute(
            "observer",
            profile,
            CandidateSemanticClass.ObserverAffecting,
            [11, 23, 47]);

        Assert.Equal(first.BudgetCurve, second.BudgetCurve);
        Assert.Equal(first.CandidateSetFingerprint, second.CandidateSetFingerprint);
    }
}
