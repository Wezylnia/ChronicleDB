using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ShadowRetentionPublicationPlanTests
{
    [Fact]
    public void DefaultPlanIsValidAndStable()
    {
        var first = ShadowRetentionPublicationPlan.CreateDefault();
        var second = ShadowRetentionPublicationPlan.CreateDefault();

        first.Validate();
        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
        Assert.Equal(3, first.Families.Count);
        Assert.Equal(4, first.SourceAnchors.Count);
        Assert.Equal(
            first.SourceAnchors.Select(anchor => anchor.AnchorId).Order(StringComparer.Ordinal),
            first.SourceAnchors.Select(anchor => anchor.AnchorId));
        Assert.Contains(first.Families, family => family.IsNegativeControlFamily);
    }

    [Fact]
    public void DefaultPlanKeepsSeedPartitionsDisjoint()
    {
        var plan = ShadowRetentionPublicationPlan.CreateDefault();

        Assert.Empty(plan.PilotSeeds.Intersect(plan.HoldoutASeeds));
        Assert.Empty(plan.PilotSeeds.Intersect(plan.HoldoutBSeeds));
        Assert.Empty(plan.HoldoutASeeds.Intersect(plan.HoldoutBSeeds));
    }

    [Fact]
    public void PlanRejectsOverlappingHoldoutSeeds()
    {
        var plan = ShadowRetentionPublicationPlan.CreateDefault() with
        {
            HoldoutBSeeds = [1101, 2102],
        };

        Assert.Throws<InvalidOperationException>(plan.Validate);
    }

    [Fact]
    public void PublicationPlanWriterIsImmutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-a1-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plan = ShadowRetentionPublicationPlan.CreateDefault();

            var artifact = ShadowRetentionPublicationPlanWriter.Write(directory, plan);
            var repeated = ShadowRetentionPublicationPlanWriter.Write(directory, plan);

            Assert.Equal(plan.ComputeCanonicalSha256(), artifact.Sha256);
            Assert.Equal(artifact, repeated);
            Assert.Throws<IOException>(() =>
            {
                _ = ShadowRetentionPublicationPlanWriter.Write(directory, plan with { ValueBytes = 1024 });
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlanRetainsExplicitLowShadowNegativeControl()
    {
        var plan = ShadowRetentionPublicationPlan.CreateDefault();
        var negative = Assert.Single(plan.Families, family => family.IsNegativeControlFamily);

        Assert.Contains(0.001, negative.ShadowFractions);
        Assert.Contains(0.01, negative.ShadowFractions);
        Assert.Contains(0.10, negative.ShadowFractions);
        Assert.Contains(plan.InterpretationRules, rule => rule.Contains("negative controls", StringComparison.Ordinal));
    }


    [Fact]
    public void SourceAnchorsSeparateObservedEvidenceFromShadowMapping()
    {
        var plan = ShadowRetentionPublicationPlan.CreateDefault();

        var decibel = Assert.Single(plan.SourceAnchors, anchor => anchor.AnchorId == "decibel-2016");
        Assert.Contains("20% updates", decibel.ObservedEvidence, StringComparison.Ordinal);
        Assert.Contains("not equivalent", decibel.MappingConstraint, StringComparison.Ordinal);

        var matrixOne = Assert.Single(plan.SourceAnchors, anchor => anchor.AnchorId == "matrixone-2026");
        Assert.Contains("600 million", matrixOne.ObservedEvidence, StringComparison.Ordinal);
        Assert.Contains("negative-control", matrixOne.CampaignRole, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "chronicle-a1-publication-plan-" + Guid.NewGuid().ToString("N"));
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
