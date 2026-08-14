using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class UnseededLocalCampaignTests
{
    [Fact]
    public void FrozenCampaignProducesFourProfilesAndAllOutcomesAreRecorded()
    {
        UnseededLocalCampaignReport report = UnseededLocalCampaign.ExecuteDefault();

        Assert.Equal(32, report.Seeds.Count);
        Assert.Equal(128, report.Runs.Count);
        Assert.False(report.ExternalEvidence);
        Assert.Contains("predeclared", report.GrammarIdentity, StringComparison.Ordinal);
        Assert.Contains("known-failure", report.OutcomeCounts.Keys);
        Assert.Contains("no-failure", report.OutcomeCounts.Keys);
        Assert.Equal(128, report.OutcomeCounts.Values.Sum());
    }

    [Fact]
    public void SameFrozenSeedsProduceByteEquivalentRunLedger()
    {
        UnseededLocalCampaignReport first = UnseededLocalCampaign.ExecuteDefault();
        UnseededLocalCampaignReport second = UnseededLocalCampaign.ExecuteDefault();

        Assert.Equal(first.Runs, second.Runs);
        Assert.Equal(first.OutcomeCounts, second.OutcomeCounts);
    }

    [Fact]
    public void TraceBudgetIsRecordedAndCannotExceedCandidateSpace()
    {
        UnseededLocalCampaignReport report = UnseededLocalCampaign.ExecuteDefault(traceBudget: 3);

        Assert.All(report.Runs, run => Assert.Equal(3, run.TraceBudget));
        Assert.All(report.Runs, run => Assert.True(run.FirstTargetIndex < 0 || run.FirstTargetIndex >= 3 || run.Outcome == "known-failure"));
    }
}
