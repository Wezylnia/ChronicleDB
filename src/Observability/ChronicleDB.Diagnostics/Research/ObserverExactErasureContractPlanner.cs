namespace ChronicleDB.Diagnostics.Research;

public enum ObserverExactErasurePlanOutcome : byte
{
    AnalysisOnly = 0,
    RequestAllowed = 1,
    BlockedByObserverContract = 2,
    BlockedByIncompleteClosure = 3,
    ForceAuthorizationRequired = 4,
    ForcePlanReadyWithExistingSemantics = 5,
    ForcePlanRequiresKeyScopedSemanticExtension = 6,
}

public enum ObserverExactErasureActionKind : byte
{
    DeleteOrTombstoneCurrentState = 0,
    RevokeGenericTimeTravelForKey = 1,
    RevokePersistentSnapshotForKey = 2,
    WaitForActiveObserverRelease = 3,
    RewriteRecoveryRepresentation = 4,
    ReclaimPhysicalRepresentation = 5,
}

public enum ObserverExactErasureExistingAlternative : byte
{
    None = 0,
    LogicalDeleteOrTombstone = 1,
    AdvanceWholeHistoryRetentionFloor = 2,
    DeleteWholeSnapshot = 3,
    WaitForObserverRelease = 4,
    RewriteRepresentation = 5,
    ReclaimRepresentation = 6,
}

public sealed record ObserverExactErasurePlanAction(
    string ActionId,
    ObserverExactErasureActionKind Kind,
    Guid? HistoryId,
    ulong? MinimumBoundary,
    ulong? MaximumBoundary,
    IReadOnlyList<string> ObserverIds,
    IReadOnlyList<string> RepresentationIds,
    bool RequiresKeyScopedSemanticExtension,
    bool RequiresQuiescence,
    ObserverExactErasureExistingAlternative ExistingAlternative);

public sealed record ObserverExactErasureContractPlan(
    ErasureMode Mode,
    ObserverExactErasurePlanOutcome Outcome,
    ObserverExactErasureOracleResult SemanticAnalysis,
    ErasureClosureAnalysis RepresentationAnalysis,
    IReadOnlyList<ObserverExactErasurePlanAction> SemanticActions,
    IReadOnlyList<ObserverExactErasurePlanAction> RepresentationActions,
    IReadOnlyList<string> BlockingHistoricalObserverIds,
    int KeyScopedSemanticExtensionActionCount,
    int CollateralWholeObserverAlternativeCount,
    bool RequiresQuiescence,
    bool ExecutableWithExistingSemantics,
    bool CanAcknowledgeAfterDurablePlanApplied);

/// <summary>
/// Research-only A8-O2 planner. It composes the observer-exact semantic witnesses
/// from A8-O1 with the existing fail-closed representation inventory. It never
/// mutates roots, histories, WAL, checkpoints, physical files, or active handles.
///
/// The planner explicitly distinguishes a candidate key-scoped revocation from the
/// conservative mechanism already available in ordinary storage systems: advancing
/// an entire history floor or deleting an entire snapshot. This distinction is a
/// falsification surface, not a production feature promise.
/// </summary>
public static class ObserverExactErasureContractPlanner
{
    public static ObserverExactErasureContractPlan Plan(
        ResearchRetentionSnapshot retentionSnapshot,
        ErasureClosureInput closureInput,
        ErasureMode mode,
        bool forceAuthorized = false)
    {
        ArgumentNullException.ThrowIfNull(retentionSnapshot);
        ArgumentNullException.ThrowIfNull(closureInput);
        if (string.IsNullOrWhiteSpace(closureInput.KeyId))
        {
            throw new ArgumentException("Observer-exact erasure planning requires a stable key ID.", nameof(closureInput));
        }

        var closureHistoryIds = closureInput.Topology.Select(node => node.HistoryId).ToHashSet();
        if (retentionSnapshot.Histories.Any(history => !closureHistoryIds.Contains(history.HistoryId)))
        {
            throw new ArgumentException(
                "Every retained observer history must be present in the erasure representation topology.",
                nameof(closureInput));
        }

        var semantic = new ObserverExactErasureOracle(retentionSnapshot).Analyze(closureInput.KeyId);
        var representation = ErasureClosureAnalyzer.Analyze(closureInput, ErasureScope.Global);
        var semanticActions = BuildSemanticActions(semantic);
        var representationActions = BuildRepresentationActions(representation);
        var historicalBlockers = semantic.BlockingObservers
            .Where(item => item.Kind != ErasureObserverContractKind.CurrentState)
            .Select(item => item.ObserverId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var extensionCount = semanticActions.Count(action => action.RequiresKeyScopedSemanticExtension);
        var collateralAlternativeCount = semanticActions.Count(action =>
            action.ExistingAlternative is ObserverExactErasureExistingAlternative.AdvanceWholeHistoryRetentionFloor
                or ObserverExactErasureExistingAlternative.DeleteWholeSnapshot);
        var requiresQuiescence = semanticActions.Any(action => action.RequiresQuiescence);
        var closureComplete = representation.ClosureIsComplete;
        var requestBlocked = historicalBlockers.Length != 0;
        var executableWithExistingSemantics = extensionCount == 0;

        var outcome = mode switch
        {
            ErasureMode.Analyze => ObserverExactErasurePlanOutcome.AnalysisOnly,
            ErasureMode.Request when !closureComplete => ObserverExactErasurePlanOutcome.BlockedByIncompleteClosure,
            ErasureMode.Request when requestBlocked => ObserverExactErasurePlanOutcome.BlockedByObserverContract,
            ErasureMode.Request => ObserverExactErasurePlanOutcome.RequestAllowed,
            ErasureMode.Force when !forceAuthorized => ObserverExactErasurePlanOutcome.ForceAuthorizationRequired,
            ErasureMode.Force when !closureComplete => ObserverExactErasurePlanOutcome.BlockedByIncompleteClosure,
            ErasureMode.Force when extensionCount != 0 => ObserverExactErasurePlanOutcome.ForcePlanRequiresKeyScopedSemanticExtension,
            ErasureMode.Force => ObserverExactErasurePlanOutcome.ForcePlanReadyWithExistingSemantics,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        var canAcknowledge = outcome is ObserverExactErasurePlanOutcome.RequestAllowed
            or ObserverExactErasurePlanOutcome.ForcePlanReadyWithExistingSemantics;

        return new ObserverExactErasureContractPlan(
            mode,
            outcome,
            semantic,
            representation,
            Array.AsReadOnly(semanticActions),
            Array.AsReadOnly(representationActions),
            Array.AsReadOnly(historicalBlockers),
            extensionCount,
            collateralAlternativeCount,
            requiresQuiescence,
            executableWithExistingSemantics,
            canAcknowledge);
    }

    private static ObserverExactErasurePlanAction[] BuildSemanticActions(ObserverExactErasureOracleResult semantic)
    {
        var actions = new List<ObserverExactErasurePlanAction>();

        foreach (var historyGroup in semantic.BlockingObservers
                     .Where(item => item.Kind == ErasureObserverContractKind.CurrentState)
                     .GroupBy(item => item.HistoryId)
                     .OrderBy(group => group.Key))
        {
            actions.Add(Action(
                $"current-delete:{historyGroup.Key:N}",
                ObserverExactErasureActionKind.DeleteOrTombstoneCurrentState,
                historyGroup.Key,
                historyGroup,
                requiresExtension: false,
                requiresQuiescence: false,
                ObserverExactErasureExistingAlternative.LogicalDeleteOrTombstone));
        }

        foreach (var historyGroup in semantic.BlockingObservers
                     .Where(item => item.Kind == ErasureObserverContractKind.GenericTimeTravel)
                     .GroupBy(item => item.HistoryId)
                     .OrderBy(group => group.Key))
        {
            actions.Add(Action(
                $"generic-key-revoke:{historyGroup.Key:N}",
                ObserverExactErasureActionKind.RevokeGenericTimeTravelForKey,
                historyGroup.Key,
                historyGroup,
                requiresExtension: true,
                requiresQuiescence: false,
                ObserverExactErasureExistingAlternative.AdvanceWholeHistoryRetentionFloor));
        }

        foreach (var blocker in semantic.BlockingObservers
                     .Where(item => item.Kind == ErasureObserverContractKind.PersistentSnapshot)
                     .OrderBy(item => item.ObserverId, StringComparer.Ordinal))
        {
            actions.Add(Action(
                $"snapshot-key-revoke:{blocker.ObserverId}",
                ObserverExactErasureActionKind.RevokePersistentSnapshotForKey,
                blocker.HistoryId,
                [blocker],
                requiresExtension: true,
                requiresQuiescence: false,
                ObserverExactErasureExistingAlternative.DeleteWholeSnapshot));
        }

        foreach (var blocker in semantic.BlockingObservers
                     .Where(item => item.Kind == ErasureObserverContractKind.ActiveBoundary)
                     .OrderBy(item => item.ObserverId, StringComparer.Ordinal))
        {
            actions.Add(Action(
                $"active-release:{blocker.ObserverId}",
                ObserverExactErasureActionKind.WaitForActiveObserverRelease,
                blocker.HistoryId,
                [blocker],
                requiresExtension: false,
                requiresQuiescence: true,
                ObserverExactErasureExistingAlternative.WaitForObserverRelease));
        }

        return actions.OrderBy(action => action.ActionId, StringComparer.Ordinal).ToArray();
    }

    private static ObserverExactErasurePlanAction[] BuildRepresentationActions(ErasureClosureAnalysis analysis)
    {
        var actions = new List<ObserverExactErasurePlanAction>();
        foreach (var group in analysis.ReachableValueRepresentations
                     .Where(item => !item.IsObserverContract)
                     .GroupBy(item => IsPhysicalRepresentation(item.Kind)
                         ? ObserverExactErasureActionKind.ReclaimPhysicalRepresentation
                         : ObserverExactErasureActionKind.RewriteRecoveryRepresentation)
                     .OrderBy(group => group.Key))
        {
            var representationIds = group
                .Select(item => item.RepresentationId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (representationIds.Length == 0)
            {
                continue;
            }
            var isPhysical = group.Key == ObserverExactErasureActionKind.ReclaimPhysicalRepresentation;
            actions.Add(new ObserverExactErasurePlanAction(
                isPhysical ? "physical-reclaim" : "authority-rewrite",
                group.Key,
                null,
                null,
                null,
                [],
                Array.AsReadOnly(representationIds),
                RequiresKeyScopedSemanticExtension: false,
                RequiresQuiescence: false,
                isPhysical
                    ? ObserverExactErasureExistingAlternative.ReclaimRepresentation
                    : ObserverExactErasureExistingAlternative.RewriteRepresentation));
        }
        return actions.ToArray();
    }


    private static bool IsPhysicalRepresentation(ErasureRepresentationKind kind)
        => kind is ErasureRepresentationKind.DerivedCurrentState or ErasureRepresentationKind.CompactionTemporary
            || kind.ToString() is "PhysicalDataRecord" or "PhysicalOverflowChunk";

    private static ObserverExactErasurePlanAction Action(
        string actionId,
        ObserverExactErasureActionKind kind,
        Guid historyId,
        IEnumerable<ObserverExactErasureWitness> observers,
        bool requiresExtension,
        bool requiresQuiescence,
        ObserverExactErasureExistingAlternative alternative)
    {
        var materialized = observers.OrderBy(item => item.ObserverId, StringComparer.Ordinal).ToArray();
        return new ObserverExactErasurePlanAction(
            actionId,
            kind,
            historyId,
            materialized.Min(item => item.Boundary),
            materialized.Max(item => item.Boundary),
            Array.AsReadOnly(materialized.Select(item => item.ObserverId).ToArray()),
            [],
            requiresExtension,
            requiresQuiescence,
            alternative);
    }
}
