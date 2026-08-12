namespace ChronicleDB.Diagnostics.Research;

public enum ObserverScopedErasureReadDecision : byte
{
    PassThrough = 0,
    RedactTargetValue = 1,
}

/// <summary>
/// Pure O3 read/recovery adapter. A durable OSEA authority applies to the target key
/// over history/boundary visibility regions, not merely to observer IDs that happened
/// to exist when the force plan was created. This prevents a later snapshot or active
/// historical handle from recreating an already-revoked target observation.
/// </summary>
public static class ObserverScopedErasureAuthorityReadFilter
{
    public static ObserverScopedErasureReadDecision Evaluate(
        ObserverScopedErasureAuthorityDescriptor descriptor,
        string keyId,
        Guid historyId,
        ulong boundary)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(keyId) || historyId == Guid.Empty)
        {
            throw new ArgumentException("Authority read filtering requires a stable key and history identity.");
        }

        if (!keyId.Equals(descriptor.KeyId, StringComparison.Ordinal))
        {
            return ObserverScopedErasureReadDecision.PassThrough;
        }

        return descriptor.VisibilityRegions.Any(region =>
                region.HistoryId == historyId
                && boundary >= region.MinimumBoundary
                && boundary <= region.MaximumBoundary)
            ? ObserverScopedErasureReadDecision.RedactTargetValue
            : ObserverScopedErasureReadDecision.PassThrough;
    }
}
