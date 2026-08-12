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

public sealed record ObserverScopedErasureAuthorityDescriptor(
    string FormatVersion,
    string KeyId,
    IReadOnlyList<ObserverScopedErasureRevocation> Revocations,
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
            quiescence,
            currentDeletes,
            preserved,
            rewrite,
            reclaim);
        return new ObserverScopedErasureAuthorityDescriptor(
            FormatVersion,
            plan.SemanticAnalysis.KeyId,
            Array.AsReadOnly(revocations),
            Array.AsReadOnly(quiescence),
            Array.AsReadOnly(currentDeletes),
            Array.AsReadOnly(preserved),
            Array.AsReadOnly(rewrite),
            Array.AsReadOnly(reclaim),
            hash);
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
