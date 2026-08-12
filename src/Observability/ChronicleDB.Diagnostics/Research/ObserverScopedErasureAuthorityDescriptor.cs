using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChronicleDB.Diagnostics.Research;

public sealed record ObserverScopedErasureRevocation(
    string ObserverId,
    ErasureObserverContractKind Kind,
    Guid HistoryId,
    ulong Boundary,
    string ResolvedVersionId,
    Guid ResolvedHistoryId,
    ulong ResolvedSequence,
    int ParentFallbackHops);

public sealed record ObserverScopedErasurePreservedObservation(
    string ObserverId,
    ErasureObserverContractKind Kind,
    Guid HistoryId,
    ulong Boundary,
    ErasureContentState Content,
    string? ResolvedVersionId,
    Guid? ResolvedHistoryId,
    ulong? ResolvedSequence,
    int ParentFallbackHops);

public sealed record ObserverScopedErasureVisibilityRegion(
    Guid HistoryId,
    ulong MinimumBoundary,
    ulong MaximumBoundary,
    IReadOnlyList<string> EvidenceObserverIds);

public sealed record ObserverScopedErasureAuthorityDescriptor(
    string FormatVersion,
    string KeyId,
    IReadOnlyList<ObserverScopedErasureRevocation> Revocations,
    IReadOnlyList<ObserverScopedErasureVisibilityRegion> VisibilityRegions,
    IReadOnlyList<string> QuiescenceObserverIds,
    IReadOnlyList<Guid> CurrentDeleteHistoryIds,
    IReadOnlyList<ObserverScopedErasurePreservedObservation> PreservedTargetObservations,
    IReadOnlyList<string> RewriteRepresentationIds,
    IReadOnlyList<string> ReclaimRepresentationIds,
    string CanonicalSha256);

/// <summary>
/// Compiles an A8-O2 force plan into the exact observer scope that an A8-O3 authority
/// would have to carry. This is the semantic bridge that prevents O3 from degenerating
/// into a generic "redact an earlier log entry" protocol: the descriptor binds the
/// target key to concrete MVCC observer witnesses, including inherited resolution.
/// </summary>
public static class ObserverScopedErasureAuthorityDescriptorCompiler
{
    private const string FormatVersion = "chronicle-a8-osea-v1";

    public static ObserverScopedErasureAuthorityDescriptor Compile(ObserverExactErasureContractPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mode != ErasureMode.Force
            || plan.Outcome != ObserverExactErasurePlanOutcome.ForcePlanRequiresKeyScopedSemanticExtension
            || plan.KeyScopedSemanticExtensionActionCount <= 0
            || !plan.RepresentationAnalysis.ClosureIsComplete)
        {
            throw new ArgumentException(
                "OSEA compilation requires an authorized, closure-complete force plan that needs key-scoped semantics.",
                nameof(plan));
        }

        var blockers = plan.SemanticAnalysis.BlockingObservers
            .ToDictionary(item => item.ObserverId, StringComparer.Ordinal);
        var actionObserverIds = plan.SemanticActions
            .SelectMany(action => action.ObserverIds)
            .ToArray();
        if (actionObserverIds.Distinct(StringComparer.Ordinal).Count() != actionObserverIds.Length
            || actionObserverIds.Any(id => !blockers.ContainsKey(id))
            || blockers.Keys.Any(id => !actionObserverIds.Contains(id, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Every blocking observer must be covered exactly once by the O2 semantic plan.",
                nameof(plan));
        }

        var revocationIds = plan.SemanticActions
            .Where(action => action.RequiresKeyScopedSemanticExtension)
            .SelectMany(action => action.ObserverIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var revocations = revocationIds
            .Select(id => ToRevocation(blockers[id]))
            .OrderBy(item => item.ObserverId, StringComparer.Ordinal)
            .ToArray();
        var visibilityRegions = BuildVisibilityRegions(plan.SemanticAnalysis);
        var quiescence = plan.SemanticActions
            .Where(action => action.Kind == ObserverExactErasureActionKind.WaitForActiveObserverRelease)
            .SelectMany(action => action.ObserverIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var currentDeletes = plan.SemanticActions
            .Where(action => action.Kind == ObserverExactErasureActionKind.DeleteOrTombstoneCurrentState)
            .Select(action => action.HistoryId ?? throw new ArgumentException("Current-state action lacks a history ID.", nameof(plan)))
            .Distinct()
            .Order()
            .ToArray();
        var preserved = plan.SemanticAnalysis.Observers
            .Where(item => !item.ReconstructsValue)
            .Select(ToPreserved)
            .OrderBy(item => item.ObserverId, StringComparer.Ordinal)
            .ToArray();
        var rewrite = plan.RepresentationActions
            .Where(action => action.Kind == ObserverExactErasureActionKind.RewriteRecoveryRepresentation)
            .SelectMany(action => action.RepresentationIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var reclaim = plan.RepresentationActions
            .Where(action => action.Kind == ObserverExactErasureActionKind.ReclaimPhysicalRepresentation)
            .SelectMany(action => action.RepresentationIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (revocations.Length == 0
            || revocations.Any(item => preserved.Any(preservedItem => preservedItem.ObserverId == item.ObserverId)))
        {
            throw new ArgumentException("OSEA requires a non-empty, disjoint exact revocation set.", nameof(plan));
        }

        var hash = ComputeHash(
            plan.SemanticAnalysis.KeyId,
            revocations,
            visibilityRegions,
            quiescence,
            currentDeletes,
            preserved,
            rewrite,
            reclaim);
        return new ObserverScopedErasureAuthorityDescriptor(
            FormatVersion,
            plan.SemanticAnalysis.KeyId,
            Array.AsReadOnly(revocations),
            Array.AsReadOnly(visibilityRegions),
            Array.AsReadOnly(quiescence),
            Array.AsReadOnly(currentDeletes),
            Array.AsReadOnly(preserved),
            Array.AsReadOnly(rewrite),
            Array.AsReadOnly(reclaim),
            hash);
    }

    private static ObserverScopedErasureVisibilityRegion[] BuildVisibilityRegions(
        ObserverExactErasureOracleResult semantic)
    {
        var raw = new List<(Guid HistoryId, ulong Minimum, ulong Maximum, string EvidenceId)>();
        foreach (var group in semantic.Observers
                     .Where(item => item.Kind == ErasureObserverContractKind.GenericTimeTravel)
                     .GroupBy(item => item.HistoryId)
                     .OrderBy(group => group.Key))
        {
            var ordered = group.OrderBy(item => item.Boundary).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var current = ordered[index];
                if (!current.ReconstructsValue)
                {
                    continue;
                }

                var maximum = current.Boundary;
                if (index + 1 < ordered.Length && ordered[index + 1].Boundary > current.Boundary)
                {
                    maximum = checked(ordered[index + 1].Boundary - 1);
                }
                raw.Add((current.HistoryId, current.Boundary, maximum, current.ObserverId));
            }
        }

        foreach (var blocker in semantic.BlockingObservers
                     .Where(item => item.Kind != ErasureObserverContractKind.GenericTimeTravel))
        {
            raw.Add((blocker.HistoryId, blocker.Boundary, blocker.Boundary, blocker.ObserverId));
        }

        var result = new List<ObserverScopedErasureVisibilityRegion>();
        foreach (var historyGroup in raw.GroupBy(item => item.HistoryId).OrderBy(group => group.Key))
        {
            var ordered = historyGroup
                .OrderBy(item => item.Minimum)
                .ThenBy(item => item.Maximum)
                .ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var minimum = ordered[0].Minimum;
            var maximum = ordered[0].Maximum;
            var evidence = new HashSet<string>(StringComparer.Ordinal) { ordered[0].EvidenceId };
            for (var index = 1; index < ordered.Length; index++)
            {
                var next = ordered[index];
                var overlapsOrAdjacent = next.Minimum <= maximum
                    || (maximum != ulong.MaxValue && next.Minimum == maximum + 1);
                if (overlapsOrAdjacent)
                {
                    maximum = Math.Max(maximum, next.Maximum);
                    evidence.Add(next.EvidenceId);
                    continue;
                }

                result.Add(Region(historyGroup.Key, minimum, maximum, evidence));
                minimum = next.Minimum;
                maximum = next.Maximum;
                evidence = new HashSet<string>(StringComparer.Ordinal) { next.EvidenceId };
            }
            result.Add(Region(historyGroup.Key, minimum, maximum, evidence));
        }
        return result.ToArray();
    }

    private static ObserverScopedErasureVisibilityRegion Region(
        Guid historyId,
        ulong minimum,
        ulong maximum,
        IEnumerable<string> evidence)
        => new(
            historyId,
            minimum,
            maximum,
            Array.AsReadOnly(evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));

    public static void Validate(ObserverScopedErasureAuthorityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.FormatVersion.Equals(FormatVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(descriptor.KeyId)
            || descriptor.Revocations.Count == 0
            || descriptor.VisibilityRegions.Count == 0
            || descriptor.CanonicalSha256.Length != 64)
        {
            throw new InvalidDataException("OSEA authority descriptor has invalid required metadata.");
        }

        if (descriptor.Revocations.Select(item => item.ObserverId).Distinct(StringComparer.Ordinal).Count() != descriptor.Revocations.Count
            || descriptor.PreservedTargetObservations.Select(item => item.ObserverId).Distinct(StringComparer.Ordinal).Count() != descriptor.PreservedTargetObservations.Count
            || descriptor.Revocations.Any(revocation => descriptor.PreservedTargetObservations.Any(preserved => preserved.ObserverId == revocation.ObserverId))
            || descriptor.VisibilityRegions.Any(region => region.HistoryId == Guid.Empty || region.MinimumBoundary > region.MaximumBoundary))
        {
            throw new InvalidDataException("OSEA authority descriptor contains an invalid or overlapping semantic scope.");
        }

        var expectedHash = ComputeHash(
            descriptor.KeyId,
            descriptor.Revocations,
            descriptor.VisibilityRegions,
            descriptor.QuiescenceObserverIds,
            descriptor.CurrentDeleteHistoryIds,
            descriptor.PreservedTargetObservations,
            descriptor.RewriteRepresentationIds,
            descriptor.ReclaimRepresentationIds);
        if (!expectedHash.Equals(descriptor.CanonicalSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("OSEA authority descriptor canonical hash does not match its contents.");
        }
    }

    private static ObserverScopedErasureRevocation ToRevocation(ObserverExactErasureWitness witness)
    {
        if (!witness.ReconstructsValue
            || witness.ResolvedVersionId is null
            || witness.ResolvedHistoryId is null
            || witness.ResolvedSequence is null)
        {
            throw new ArgumentException("A revocation must be backed by a concrete value-reconstructing witness.", nameof(witness));
        }

        return new ObserverScopedErasureRevocation(
            witness.ObserverId,
            witness.Kind,
            witness.HistoryId,
            witness.Boundary,
            witness.ResolvedVersionId,
            witness.ResolvedHistoryId.Value,
            witness.ResolvedSequence.Value,
            witness.ParentFallbackHops);
    }

    private static ObserverScopedErasurePreservedObservation ToPreserved(ObserverExactErasureWitness witness)
        => new(
            witness.ObserverId,
            witness.Kind,
            witness.HistoryId,
            witness.Boundary,
            witness.Content,
            witness.ResolvedVersionId,
            witness.ResolvedHistoryId,
            witness.ResolvedSequence,
            witness.ParentFallbackHops);

    private static string ComputeHash(
        string keyId,
        IReadOnlyList<ObserverScopedErasureRevocation> revocations,
        IReadOnlyList<ObserverScopedErasureVisibilityRegion> visibilityRegions,
        IReadOnlyList<string> quiescence,
        IReadOnlyList<Guid> currentDeletes,
        IReadOnlyList<ObserverScopedErasurePreservedObservation> preserved,
        IReadOnlyList<string> rewrite,
        IReadOnlyList<string> reclaim)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FormatVersion);
        builder.Append("key|").AppendLine(keyId);
        foreach (var item in revocations)
        {
            builder.Append("revoke|").Append(item.ObserverId).Append('|').Append((byte)item.Kind).Append('|')
                .Append(item.HistoryId.ToString("N")).Append('|').Append(item.Boundary).Append('|')
                .Append(item.ResolvedVersionId).Append('|').Append(item.ResolvedHistoryId.ToString("N")).Append('|')
                .Append(item.ResolvedSequence).Append('|').Append(item.ParentFallbackHops).AppendLine();
        }
        foreach (var region in visibilityRegions)
        {
            builder.Append("region|").Append(region.HistoryId.ToString("N")).Append('|')
                .Append(region.MinimumBoundary).Append('|').Append(region.MaximumBoundary).Append('|')
                .AppendJoin(',', region.EvidenceObserverIds).AppendLine();
        }
        foreach (var id in quiescence)
        {
            builder.Append("quiesce|").AppendLine(id);
        }
        foreach (var id in currentDeletes)
        {
            builder.Append("delete-current|").AppendLine(id.ToString("N"));
        }
        foreach (var item in preserved)
        {
            builder.Append("preserve|").Append(item.ObserverId).Append('|').Append((byte)item.Kind).Append('|')
                .Append(item.HistoryId.ToString("N")).Append('|').Append(item.Boundary).Append('|')
                .Append((byte)item.Content).Append('|').Append(item.ResolvedVersionId ?? "-").Append('|')
                .Append(item.ResolvedHistoryId?.ToString("N") ?? "-").Append('|')
                .Append(item.ResolvedSequence?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|').Append(item.ParentFallbackHops).AppendLine();
        }
        foreach (var id in rewrite)
        {
            builder.Append("rewrite|").AppendLine(id);
        }
        foreach (var id in reclaim)
        {
            builder.Append("reclaim|").AppendLine(id);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
