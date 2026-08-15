using System.Text.Json;
using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class ExternalUnseededCampaignTests
{
    [Fact]
    public void FrozenExternalReplayPreservesFailureClassesAndIsExplicitlyNotLiveRerun()
    {
        string directory = Path.Combine(Path.GetTempPath(), "branchcheck-external-unseeded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string matrix = Write(directory, "matrix.json", """
                {"Report":{"Recipes":[
                  {"Recipe":"ordinary","IdentityStateRelevant":false,"TriggeredBoundaryViolation":false,"GenericStateBaseline":"Pass"},
                  {"Recipe":"identity","IdentityStateRelevant":true,"TriggeredBoundaryViolation":true,"GenericStateBaseline":"Pass"}]}}
                """);
            string dolt223 = Write(directory, "dolt223.json", """
                {"Report":{"Recipes":[
                  {"Recipe":"control","SequenceStateRelevant":false,"TriggeredViolation":false,"GenericStateBaseline":"Pass","ContinuationRelationEvidence":""},
                  {"Recipe":"history","SequenceStateRelevant":true,"TriggeredViolation":true,"GenericStateBaseline":"Detected","ContinuationRelationEvidence":"token diverged"}]}}
                """);
            string dolt230 = Write(directory, "dolt230.json", """
                {"Report":{"Recipes":[
                  {"Recipe":"control","SequenceStateRelevant":false,"TriggeredViolation":true,"GenericStateBaseline":"Detected","ContinuationRelationEvidence":"control"}]}}
                """);
            string slateBuggy = Write(directory, "slate-buggy.json", """
                {"Report":{"Candidates":[
                  {"Candidate":"reader","DependencyRelevant":false,"ViolatesExpectedReadability":false,"Error":null},
                  {"Candidate":"clone-reader","DependencyRelevant":true,"ViolatesExpectedReadability":true,"Error":"missing object"}]}}
                """);
            string slateFixed = Write(directory, "slate-fixed.json", """
                {"Report":{"Candidates":[
                  {"Candidate":"reader","DependencyRelevant":false,"ViolatesExpectedReadability":false,"Error":null}]}}
                """);

            ExternalUnseededCampaignReport report = ExternalUnseededCampaign.ExecuteFromFrozenArtifacts(
                matrix,
                dolt223,
                dolt230,
                slateBuggy,
                slateFixed,
                traceBudget: 2,
                timeBudgetMilliseconds: 10);

            Assert.True(report.ExternalEvidence);
            Assert.True(report.ReplayFromFrozenCandidateObservations);
            Assert.False(report.LiveBackendReruns);
            Assert.Equal(ExternalUnseededCampaign.FrozenSeeds.Count * 5, report.Runs.Count);
            Assert.True(report.OutcomeCounts[nameof(ExternalUnseededOutcome.KnownFailure)] > 0);
            Assert.True(report.OutcomeCounts[nameof(ExternalUnseededOutcome.DuplicateRootCause)] > 0);
            Assert.True(report.OutcomeCounts[nameof(ExternalUnseededOutcome.FalsePositive)] > 0);
            Assert.True(report.OutcomeCounts[nameof(ExternalUnseededOutcome.NoFailure)] > 0);
            Assert.Contains(nameof(ExternalUnseededOutcome.NewRootCauseCandidate), report.OutcomeCounts.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Write(string directory, string name, string json)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, json);
        using (JsonDocument.Parse(json)) { }
        return path;
    }
}
