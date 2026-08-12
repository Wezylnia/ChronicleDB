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
