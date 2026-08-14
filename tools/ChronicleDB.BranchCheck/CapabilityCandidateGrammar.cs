using System.Security.Cryptography;
using System.Text;

namespace ChronicleDB.BranchCheck;

[Flags]
public enum CandidateSemanticClass
{
    Ordinary = 1,
    IdentityAffecting = 2,
    AllocatorAffecting = 4,
    DependencyAffecting = 8,
    LifecycleAffecting = 16,
    RecoveryAffecting = 32,
    ObserverAffecting = 64,
}

public sealed record CapabilityCandidate(
    string Id,
    string SourceMutation,
    string Continuation,
    TraceOperationClass OperationClass,
    CandidateSemanticClass SemanticClasses);

/// <summary>
/// Produces a capability-derived candidate space. Candidate identifiers describe
/// semantic operations only; no historical issue or reproducer identifier is used.
/// </summary>
public static class CapabilityCandidateGrammar
{
    public static IReadOnlyList<CapabilityCandidate> Generate(BranchCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<CapabilityCandidate> candidates =
        [
            Candidate("read", "none", "read", TraceOperationClass.GenericRead, CandidateSemanticClass.Ordinary),
            Candidate("insert", "none", "insert", TraceOperationClass.GenericMutation, CandidateSemanticClass.Ordinary),
            Candidate("update", "none", "update", TraceOperationClass.GenericMutation, CandidateSemanticClass.Ordinary),
            Candidate("delete-row", "none", "delete-row", TraceOperationClass.GenericMutation, CandidateSemanticClass.Ordinary),
            Candidate("alter-schema", "none", "alter-schema", TraceOperationClass.GenericMutation, CandidateSemanticClass.DependencyAffecting),
            Candidate("create-index", "none", "create-index", TraceOperationClass.GenericMutation, CandidateSemanticClass.DependencyAffecting),
            Candidate("drop-index", "none", "drop-index", TraceOperationClass.GenericMutation, CandidateSemanticClass.DependencyAffecting),
            Candidate("create-generated-identity", "none", "create-generated-identity", TraceOperationClass.ContinuationProbe, CandidateSemanticClass.IdentityAffecting | CandidateSemanticClass.AllocatorAffecting),
            Candidate("advance-allocator", "none", "advance-allocator", TraceOperationClass.ContinuationProbe, CandidateSemanticClass.AllocatorAffecting),
            Candidate("create-dependent-object", "none", "create-dependent-object", TraceOperationClass.GenericMutation, CandidateSemanticClass.DependencyAffecting),
        ];

        if (profile.SupportsHistoricalFork)
        {
            candidates.Add(Candidate(
                "drop-recreate-same-name",
                "drop-and-recreate",
                "historical-read",
                TraceOperationClass.BranchSpecificHistory,
                CandidateSemanticClass.IdentityAffecting | CandidateSemanticClass.DependencyAffecting));
            candidates.Add(Candidate(
                "rename-recreate",
                "rename-and-recreate",
                "historical-diff",
                TraceOperationClass.BranchSpecificHistory,
                CandidateSemanticClass.IdentityAffecting | CandidateSemanticClass.DependencyAffecting));
            candidates.Add(Candidate(
                "nested-historical-branch",
                "none",
                "branch-again",
                TraceOperationClass.BranchSpecificHistory,
                CandidateSemanticClass.IdentityAffecting | CandidateSemanticClass.DependencyAffecting));
            candidates.Add(Candidate(
                "historical-diff",
                "none",
                "diff",
                TraceOperationClass.BranchSpecificHistory,
                CandidateSemanticClass.IdentityAffecting));
        }

        if (profile.EquivalentObservers.Count > 1)
        {
            candidates.Add(Candidate(
                "alternate-observer",
                "none",
                "observe-alternate",
                TraceOperationClass.ObserverRead,
                CandidateSemanticClass.ObserverAffecting | CandidateSemanticClass.DependencyAffecting));
        }

        if (profile.SupportsDelete)
        {
            candidates.Add(Candidate(
                "delete-branch",
                "none",
                "delete-branch",
                TraceOperationClass.BranchSpecificLifecycle,
                CandidateSemanticClass.LifecycleAffecting | CandidateSemanticClass.DependencyAffecting));
            candidates.Add(Candidate(
                "detach",
                "none",
                "detach",
                TraceOperationClass.BranchSpecificLifecycle,
                CandidateSemanticClass.LifecycleAffecting));
            candidates.Add(Candidate(
                "branch-again-after-delete",
                "delete-source",
                "branch-again",
                TraceOperationClass.BranchSpecificLifecycle,
                CandidateSemanticClass.LifecycleAffecting | CandidateSemanticClass.DependencyAffecting));
        }

        if (profile.SupportsRestart)
        {
            candidates.Add(Candidate(
                "restart",
                "none",
                "restart",
                TraceOperationClass.Restart,
                CandidateSemanticClass.RecoveryAffecting));
            candidates.Add(Candidate(
                "retry-after-restart",
                "none",
                "retry",
                TraceOperationClass.Restart,
                CandidateSemanticClass.RecoveryAffecting | CandidateSemanticClass.LifecycleAffecting));
        }

        return candidates;
    }

    public static IReadOnlyList<CapabilityCandidate> UniformOrdering(
        BranchCapabilityProfile profile,
        int seed)
    {
        CapabilityCandidate[] candidates = Generate(profile).ToArray();
        Random random = new(seed);
        for (int i = candidates.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        return candidates;
    }

    public static IReadOnlyList<CapabilityCandidate> GuidedOrdering(
        BranchCapabilityProfile profile,
        int seed)
        => GuidedOrdering(profile, CandidateSemanticClass.Ordinary, seed);

    public static IReadOnlyList<CapabilityCandidate> GuidedOrdering(
        BranchCapabilityProfile profile,
        CandidateSemanticClass targetClasses,
        int seed)
    {
        Random random = new(seed);
        return Generate(profile)
            .Select(candidate => (Candidate: candidate, TieBreak: random.Next()))
            .OrderBy(item => SemanticPriority(item.Candidate.SemanticClasses, targetClasses))
            .ThenBy(static item => item.TieBreak)
            .Select(static item => item.Candidate)
            .ToArray();
    }

    public static string Fingerprint(BranchCapabilityProfile profile)
    {
        string canonical = string.Join(
            '\n',
            Generate(profile).Select(static candidate => string.Join(
                '|',
                candidate.Id,
                candidate.SourceMutation,
                candidate.Continuation,
                candidate.OperationClass,
                candidate.SemanticClasses)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static CapabilityCandidate Candidate(
        string id,
        string sourceMutation,
        string continuation,
        TraceOperationClass operationClass,
        CandidateSemanticClass semanticClasses)
        => new(id, sourceMutation, continuation, operationClass, semanticClasses);

    private static int SemanticPriority(
        CandidateSemanticClass classes,
        CandidateSemanticClass targetClasses = CandidateSemanticClass.Ordinary)
    {
        if (targetClasses != CandidateSemanticClass.Ordinary
            && (classes & targetClasses) != 0)
        {
            return 0;
        }

        return classes switch
        {
            var value when value.HasFlag(CandidateSemanticClass.IdentityAffecting) => 1,
            var value when value.HasFlag(CandidateSemanticClass.AllocatorAffecting) => 2,
            var value when value.HasFlag(CandidateSemanticClass.DependencyAffecting) => 3,
            var value when value.HasFlag(CandidateSemanticClass.LifecycleAffecting) => 4,
            var value when value.HasFlag(CandidateSemanticClass.RecoveryAffecting) => 5,
            var value when value.HasFlag(CandidateSemanticClass.ObserverAffecting) => 6,
            _ => 7,
        };
    }
}
