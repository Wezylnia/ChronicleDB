namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Closed-form effect model for the controlled staggered-branch A1 workload.
/// Assumptions: equal payload sizes, one current Main value per key, one distinct
/// parent predecessor protected by each branch base, and at most one local shadow
/// per branch/key. It is an experiment oracle, not a production sizing model.
/// </summary>
public static class ShadowRetentionEffectModel
{
    public static double? MinimumShadowFractionForRatio(
        int branchCount,
        double tombstoneFraction,
        double targetRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(branchCount);
        if (tombstoneFraction is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(tombstoneFraction));
        }

        if (!double.IsFinite(targetRatio) || targetRatio < 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRatio));
        }

        if (targetRatio == 1d)
        {
            return 0d;
        }

        var numerator = (targetRatio - 1d) * (branchCount + 1d);
        var denominator = branchCount * (1d - tombstoneFraction + (targetRatio * tombstoneFraction));
        var fraction = numerator / denominator;
        return fraction <= 1d ? fraction : null;
    }

    public static ShadowRetentionEffectPrediction Predict(
        int keyCount,
        int branchCount,
        double shadowFraction,
        double tombstoneFraction,
        int valueBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(branchCount);

        if (shadowFraction is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(shadowFraction));
        }

        if (tombstoneFraction is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(tombstoneFraction));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueBytes);

        var mainPayload = (double)keyCount * valueBytes;
        var parentPayload = mainPayload * branchCount;
        var shadowPayload = parentPayload * shadowFraction * (1d - tombstoneFraction);
        var releasedParentPayload = parentPayload * shadowFraction;
        var baselinePayload = mainPayload + parentPayload + shadowPayload;
        var candidatePayload = baselinePayload - releasedParentPayload;
        var ratio = candidatePayload == 0d
            ? double.PositiveInfinity
            : baselinePayload / candidatePayload;

        return new ShadowRetentionEffectPrediction(
            BaselinePayloadBytes: baselinePayload,
            ShadowAwarePayloadBytes: candidatePayload,
            ReleasedParentPayloadBytes: releasedParentPayload,
            ShadowAwareReclamationRatio: ratio);
    }
}

public sealed record ShadowRetentionEffectPrediction(
    double BaselinePayloadBytes,
    double ShadowAwarePayloadBytes,
    double ReleasedParentPayloadBytes,
    double ShadowAwareReclamationRatio);
