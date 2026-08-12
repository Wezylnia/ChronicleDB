namespace ChronicleDB.Diagnostics.Research;

public enum ErasureScope : byte
{
    Local = 0,
    Subtree = 1,
    Global = 2,
}

public enum ErasureMode : byte
{
    Analyze = 0,
    Request = 1,
    Force = 2,
}

public enum ErasureRepresentationKind : byte
{
    MvccVersion = 0,
    PersistentSnapshotRoot = 1,
    BranchBaseRoot = 2,
    WalMutation = 3,
    CheckpointVersion = 4,
    DerivedCurrentState = 5,
    CompactionTemporary = 6,
    ActiveTransactionRoot = 7,
    PhysicalDataRecord = 8,
    PhysicalOverflowChunk = 9,
}

public enum ErasureContentState : byte
{
    Absent = 0,
    Tombstone = 1,
    Value = 2,
    Unknown = 3,
}

public enum ErasureContractOutcome : byte
{
    AnalysisOnly = 0,
    Allowed = 1,
    BlockedByObserverContract = 2,
    BlockedByIncompleteClosure = 3,
    ForceAuthorizationRequired = 4,
    ForcePlanReady = 5,
}

public sealed record ErasureHistoryNode(Guid HistoryId, Guid? ParentHistoryId);

/// <summary>
/// One engine-controlled representation or observer contract relevant to an erasure
/// query. OwnerHistoryId determines scope; ProtectedHistoryId identifies the history
/// whose state an observer contract can reconstruct.
/// </summary>
public sealed record ErasureRepresentation(
    string RepresentationId,
    ErasureRepresentationKind Kind,
    Guid OwnerHistoryId,
    Guid ProtectedHistoryId,
    ulong? Sequence,
    ErasureContentState Content,
    bool IsObserverContract)
{
    public bool ReconstructsValue => Content == ErasureContentState.Value;
}

public sealed record ErasureClosureInput(
    string KeyId,
    Guid OriginHistoryId,
    IReadOnlyList<ErasureHistoryNode> Topology,
    IReadOnlyList<ErasureRepresentation> Representations,
    bool PhysicalRepresentationScanComplete,
    IReadOnlyList<string> UnscannedPhysicalRepresentations);

public sealed record ErasureClosureAnalysis(
    ErasureScope Scope,
    IReadOnlyList<Guid> ObserverHistoriesInScope,
    IReadOnlyList<ErasureRepresentation> ReachableValueRepresentations,
    IReadOnlyList<ErasureRepresentation> BlockingObserverContracts,
    IReadOnlyList<ErasureRepresentation> UnknownRepresentations,
    int MvccVersionOccurrences,
    int SnapshotRootOccurrences,
    int BranchBaseOccurrences,
    int WalOccurrences,
    int CheckpointOccurrences,
    int DerivedStateOccurrences,
    int CompactionTemporaryOccurrences,
    int PhysicalDataRecordOccurrences,
    int PhysicalOverflowChunkOccurrences,
    bool PhysicalRepresentationScanComplete,
    IReadOnlyList<string> UnscannedPhysicalRepresentations)
{
    public bool HasBlockingObserverContracts => BlockingObserverContracts.Count != 0;

    public bool ClosureIsComplete => PhysicalRepresentationScanComplete && UnknownRepresentations.Count == 0;
}

public sealed record ErasureContractDecision(
    ErasureMode Mode,
    ErasureContractOutcome Outcome,
    ErasureClosureAnalysis Analysis,
    IReadOnlyList<string> RequiredRevocations,
    IReadOnlyList<string> ProposedRewritePlan,
    bool CanAcknowledgeAfterPlanApplied);

/// <summary>
/// Pure research analyzer for Candidate 8. It never performs deletion or revocation;
/// it only computes observer/representation closure under an explicit scope.
/// </summary>
public static class ErasureClosureAnalyzer
{
    public static ErasureClosureAnalysis Analyze(ErasureClosureInput input, ErasureScope scope)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        var observerHistories = ResolveScope(input, scope);
        var scopeSet = observerHistories.ToHashSet();
        var scoped = input.Representations
            .Where(representation => scopeSet.Contains(representation.OwnerHistoryId))
            .OrderBy(representation => representation.Kind)
            .ThenBy(representation => representation.RepresentationId, StringComparer.Ordinal)
            .ToArray();
        var values = scoped.Where(representation => representation.ReconstructsValue).ToArray();
        var blockers = scoped.Where(representation => representation.IsObserverContract && representation.ReconstructsValue).ToArray();
        var unknown = scoped.Where(representation => representation.Content == ErasureContentState.Unknown).ToArray();

        return new ErasureClosureAnalysis(
            scope,
            Array.AsReadOnly(observerHistories),
            Array.AsReadOnly(values),
            Array.AsReadOnly(blockers),
            Array.AsReadOnly(unknown),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.MvccVersion && item.ReconstructsValue),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.PersistentSnapshotRoot && item.ReconstructsValue),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.BranchBaseRoot && item.ReconstructsValue),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.WalMutation && item.ReconstructsValue),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.CheckpointVersion && item.ReconstructsValue),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.DerivedCurrentState && item.ReconstructsValue),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.CompactionTemporary),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.PhysicalDataRecord),
            scoped.Count(item => item.Kind == ErasureRepresentationKind.PhysicalOverflowChunk),
            input.PhysicalRepresentationScanComplete,
            input.UnscannedPhysicalRepresentations);
    }

    private static Guid[] ResolveScope(ErasureClosureInput input, ErasureScope scope)
    {
        if (scope == ErasureScope.Global)
        {
            return input.Topology.Select(node => node.HistoryId).Order().ToArray();
        }

        if (scope == ErasureScope.Local)
        {
            return [input.OriginHistoryId];
        }

        var children = input.Topology
            .Where(node => node.ParentHistoryId.HasValue)
            .GroupBy(node => node.ParentHistoryId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(node => node.HistoryId).ToArray());
        var result = new List<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(input.OriginHistoryId);
        while (queue.Count != 0)
        {
            var current = queue.Dequeue();
            result.Add(current);
            if (children.TryGetValue(current, out var descendants))
            {
                foreach (var descendant in descendants.Order())
                {
                    queue.Enqueue(descendant);
                }
            }
        }

        return result.ToArray();
    }

    private static void Validate(ErasureClosureInput input)
    {
        if (string.IsNullOrWhiteSpace(input.KeyId))
        {
            throw new ArgumentException("Erasure closure requires a stable key ID.", nameof(input));
        }

        if (input.OriginHistoryId == Guid.Empty)
        {
            throw new ArgumentException("Erasure closure requires a valid origin history.", nameof(input));
        }

        var ids = new HashSet<Guid>();
        foreach (var node in input.Topology)
        {
            if (node.HistoryId == Guid.Empty || !ids.Add(node.HistoryId) || node.ParentHistoryId == node.HistoryId)
            {
                throw new ArgumentException("Erasure topology must contain unique valid history identities.", nameof(input));
            }
        }

        if (!ids.Contains(input.OriginHistoryId))
        {
            throw new ArgumentException("Origin history is absent from the erasure topology.", nameof(input));
        }

        foreach (var node in input.Topology)
        {
            if (node.ParentHistoryId is { } parent && !ids.Contains(parent))
            {
                throw new ArgumentException("Every erasure topology parent must be present.", nameof(input));
            }
        }

        foreach (var representation in input.Representations)
        {
            if (string.IsNullOrWhiteSpace(representation.RepresentationId)
                || !ids.Contains(representation.OwnerHistoryId)
                || !ids.Contains(representation.ProtectedHistoryId))
            {
                throw new ArgumentException("Erasure representations must reference valid histories and IDs.", nameof(input));
            }
        }
    }
}

public static class ErasureContractEvaluator
{
    public static ErasureContractDecision Evaluate(
        ErasureClosureInput input,
        ErasureScope scope,
        ErasureMode mode,
        bool forceAuthorized = false)
    {
        var analysis = ErasureClosureAnalyzer.Analyze(input, scope);
        var revocations = analysis.BlockingObserverContracts
            .Select(item => item.RepresentationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var rewrites = analysis.ReachableValueRepresentations
            .Where(item => !item.IsObserverContract)
            .Select(item => item.RepresentationId)
            .Concat(analysis.UnknownRepresentations.Select(item => item.RepresentationId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return mode switch
        {
            ErasureMode.Analyze => Decision(ErasureContractOutcome.AnalysisOnly, false),
            ErasureMode.Request when analysis.HasBlockingObserverContracts
                => Decision(ErasureContractOutcome.BlockedByObserverContract, false),
            ErasureMode.Request when !analysis.ClosureIsComplete
                => Decision(ErasureContractOutcome.BlockedByIncompleteClosure, false),
            ErasureMode.Request => Decision(ErasureContractOutcome.Allowed, true),
            ErasureMode.Force when !forceAuthorized
                => Decision(ErasureContractOutcome.ForceAuthorizationRequired, false),
            ErasureMode.Force when !analysis.ClosureIsComplete
                => Decision(ErasureContractOutcome.BlockedByIncompleteClosure, false),
            ErasureMode.Force => Decision(ErasureContractOutcome.ForcePlanReady, true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        ErasureContractDecision Decision(ErasureContractOutcome outcome, bool canAcknowledge)
            => new(
                mode,
                outcome,
                analysis,
                Array.AsReadOnly(revocations),
                Array.AsReadOnly(rewrites),
                canAcknowledge);
    }
}
