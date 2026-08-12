namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Closed-form effect model for the controlled staggered-branch A1 workload.
/// Assumptions: equal payload sizes, one current Main value per key, one distinct
/// parent predecessor protected by each branch base, and at most one local shadow
/// per branch/key. It is an experiment oracle, not a production sizing model.
/// </summary>
public static class ShadowRetentionEffectModel
{
    public static ShadowRetentionEffectPrediction PredictHeterogeneous(
        int keyCount,
        IReadOnlyList<ShadowRetentionBranchProfile> branches,
        int valueBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyCount);
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Count == 0)
        {
            throw new ArgumentException("At least one branch profile is required.", nameof(branches));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueBytes);
        foreach (var branch in branches)
        {
            branch.Validate();
        }

        var mainPayload = (double)keyCount * valueBytes;
        var baselinePayload = mainPayload;
        var candidatePayload = mainPayload;
        var releasedParentPayload = 0d;
        foreach (var branch in branches)
        {
            var parentPayload = mainPayload;
            var localShadowPayload = mainPayload
                * branch.ShadowFraction
                * (1d - branch.TombstoneFraction);
            var released = mainPayload * branch.ShadowFraction;

            baselinePayload += parentPayload + localShadowPayload;
            candidatePayload += parentPayload + localShadowPayload - released;
            releasedParentPayload += released;
        }

        return new ShadowRetentionEffectPrediction(
            BaselinePayloadBytes: baselinePayload,
            ShadowAwarePayloadBytes: candidatePayload,
            ReleasedParentPayloadBytes: releasedParentPayload,
            ShadowAwareReclamationRatio: baselinePayload / candidatePayload);
    }

    public static ShadowRetentionEffectPrediction PredictNested(
        int keyCount,
        int depth,
        double shadowFraction,
        int valueBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        if (shadowFraction is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(shadowFraction));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueBytes);

        var mainPayload = (double)keyCount * valueBytes;
        var shadowPayloadPerEdge = mainPayload * shadowFraction;
        var releasedParentPayload = shadowPayloadPerEdge * depth;
        var baselinePayload = mainPayload + (2d * releasedParentPayload);
        var candidatePayload = baselinePayload - releasedParentPayload;
        return new ShadowRetentionEffectPrediction(
            BaselinePayloadBytes: baselinePayload,
            ShadowAwarePayloadBytes: candidatePayload,
            ReleasedParentPayloadBytes: releasedParentPayload,
            ShadowAwareReclamationRatio: baselinePayload / candidatePayload);
    }

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

public sealed record ShadowRetentionBranchProfile(double ShadowFraction, double TombstoneFraction)
{
    internal void Validate()
    {
        if (ShadowFraction is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(ShadowFraction));
        }

        if (TombstoneFraction is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(TombstoneFraction));
        }
    }
}
