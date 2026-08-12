using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ShadowRetentionEffectModelTests
{
    [Theory]
    [InlineData(1, 1.50)]
    [InlineData(2, 1.6666666666666667)]
    [InlineData(4, 1.80)]
    [InlineData(8, 1.8888888888888888)]
    [InlineData(16, 1.9411764705882353)]
    public void FullOverwriteApproachesButDoesNotExceedTwoX(int branches, double expected)
    {
        var result = ShadowRetentionEffectModel.Predict(1024, branches, 1d, 0d, 4096);

        Assert.Equal(expected, result.ShadowAwareReclamationRatio, precision: 12);
        Assert.True(result.ShadowAwareReclamationRatio < 2d);
    }

    [Fact]
    public void FullTombstoneAmplificationEqualsBranchCountPlusOne()
    {
        const int branches = 8;
        var result = ShadowRetentionEffectModel.Predict(1024, branches, 1d, 1d, 4096);

        Assert.Equal(branches + 1d, result.ShadowAwareReclamationRatio, precision: 12);
    }

    [Fact]
    public void HeterogeneousProfilesMatchEquivalentManualPayloadAccounting()
    {
        var profiles = new[]
        {
            new ShadowRetentionBranchProfile(0.10, 0.00),
            new ShadowRetentionBranchProfile(0.25, 0.20),
            new ShadowRetentionBranchProfile(0.50, 1.00),
        };

        var result = ShadowRetentionEffectModel.PredictHeterogeneous(100, profiles, 10);
        var main = 1000d;
        var expectedBaseline = main
            + (main + 100d)
            + (main + 200d)
            + main;
        var expectedRelease = 100d + 250d + 500d;

        Assert.Equal(expectedBaseline, result.BaselinePayloadBytes, precision: 12);
        Assert.Equal(expectedRelease, result.ReleasedParentPayloadBytes, precision: 12);
        Assert.Equal(expectedBaseline - expectedRelease, result.ShadowAwarePayloadBytes, precision: 12);
        Assert.Equal(expectedBaseline / (expectedBaseline - expectedRelease), result.ShadowAwareReclamationRatio, precision: 12);
    }

    [Theory]
    [InlineData(8, 0.0, 1.10, 0.1125)]
    [InlineData(8, 0.0, 1.25, 0.28125)]
    [InlineData(8, 0.0, 1.50, 0.5625)]
    [InlineData(8, 1.0, 2.00, 0.5625)]
    [InlineData(8, 1.0, 9.00, 1.0)]
    public void BenefitFrontierReturnsMinimumShadowFraction(
        int branches,
        double tombstoneFraction,
        double targetRatio,
        double expected)
    {
        var result = ShadowRetentionEffectModel.MinimumShadowFractionForRatio(
            branches,
            tombstoneFraction,
            targetRatio);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, precision: 12);
    }

    [Fact]
    public void BenefitFrontierReturnsNullWhenTargetIsUnreachable()
    {
        Assert.Null(ShadowRetentionEffectModel.MinimumShadowFractionForRatio(8, 0d, 2d));
        Assert.Null(ShadowRetentionEffectModel.MinimumShadowFractionForRatio(8, 1d, 10d));
    }

    [Theory]
    [InlineData(8, 0.01, 1.008888888888889)]
    [InlineData(8, 0.10, 1.088888888888889)]
    [InlineData(8, 0.25, 1.2222222222222223)]
    [InlineData(8, 0.50, 1.4444444444444444)]
    public void PartialOverwritePredictsControlledScaleCurve(int branches, double shadow, double expected)
    {
        var result = ShadowRetentionEffectModel.Predict(1024, branches, shadow, 0d, 4096);

        Assert.Equal(expected, result.ShadowAwareReclamationRatio, precision: 12);
    }
}
