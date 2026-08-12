using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ShadowRetentionPilotExecutionPlanTests
{
    [Fact]
    public void DefaultPublicationPlanProducesCompleteDeterministicPilotOrder()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var first = ShadowRetentionPilotExecutionPlan.Create(publication);
        var second = ShadowRetentionPilotExecutionPlan.Create(publication);

        Assert.Equal(158, first.SweepRunCount);
        Assert.Equal(135, first.RepeatedRunCount);
        Assert.Equal(293, first.Runs.Count);
        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
        Assert.Equal(Enumerable.Range(0, first.Runs.Count), first.Runs.Select(run => run.TrialOrder));
        Assert.Contains(first.Runs, run => run.CaseId.Contains("low-shadow-negative-control", StringComparison.Ordinal));
        Assert.Contains(first.Runs, run => run.CaseId == "pilot-neg-b08-s001");
    }

    [Fact]
    public void ExecutionPlanChangesWhenPublicationPlanChanges()
    {
        var original = ShadowRetentionPublicationPlan.CreateDefault();
        var changed = original with { PilotSweepSeed = 302 };

        var first = ShadowRetentionPilotExecutionPlan.Create(original);
        var second = ShadowRetentionPilotExecutionPlan.Create(changed);

        Assert.NotEqual(first.PublicationPlanSha256, second.PublicationPlanSha256);
        Assert.NotEqual(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
    }

    [Fact]
    public void ExecutionPlanArtifactIsImmutable()
    {
        using var directory = new TemporaryDirectory();
        var plan = ShadowRetentionPilotExecutionPlan.Create(ShadowRetentionPublicationPlan.CreateDefault());
        var first = ShadowRetentionPilotExecutionPlanWriter.Write(directory.Path, plan);
        var repeated = ShadowRetentionPilotExecutionPlanWriter.Write(directory.Path, plan);

        Assert.Equal(first.Sha256, repeated.Sha256);
        Assert.Throws<IOException>(() =>
        {
            _ = ShadowRetentionPilotExecutionPlanWriter.Write(
                directory.Path,
                plan with { CandidateId = "changed-candidate" });
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "chronicle-a1-pilot-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
