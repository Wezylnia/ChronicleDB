namespace ChronicleDB.BranchCheck;

[Flags]
public enum CreationEvidenceKind
{
    None = 0,
    Values = 1,
    Schema = 2,
    VisibleMetadata = 4,
    All = Values | Schema | VisibleMetadata,
}

public enum RelationFamily
{
    ContinuationState,
    TemporalBoundary,
    Lifecycle,
    ObserverDependency,
    Recovery,
}

public enum RelationStatus
{
    Pass,
    Fail,
    NotApplicable,
    Inconclusive,
}

public enum BaselineStatus
{
    Pass,
    Detected,
    NotApplicable,
    Inconclusive,
}

public enum OutcomeClass
{
    Success,
    NotFound,
    Rejected,
    Corruption,
    Crash,
}

public enum TraceOperationClass
{
    Other,
    GenericRead,
    GenericMutation,
    ContinuationProbe,
    BranchSpecificLifecycle,
    BranchSpecificHistory,
    ObserverRead,
    Restart,
}

public readonly record struct BranchBoundary(string HistoryId, long Sequence)
{
    public override string ToString() => $"{HistoryId}@{Sequence}";
}

public sealed record BranchCapabilityProfile(
    string BackendName,
    bool SupportsHistoricalFork,
    bool SupportsRestart,
    bool SupportsDelete,
    IReadOnlySet<string> EquivalentObservers,
    IReadOnlySet<string> SourceBoundaryComponents)
{
    public static BranchCapabilityProfile Create(
        string backendName,
        bool supportsHistoricalFork = false,
        bool supportsRestart = false,
        bool supportsDelete = false,
        string[]? equivalentObservers = null,
        string[]? sourceBoundaryComponents = null)
        => new(
            backendName,
            supportsHistoricalFork,
            supportsRestart,
            supportsDelete,
            new HashSet<string>(equivalentObservers ?? [], StringComparer.Ordinal),
            new HashSet<string>(sourceBoundaryComponents ?? [], StringComparer.Ordinal));
}

public sealed record CanonicalState(
    IReadOnlyDictionary<string, string> Values,
    string SchemaFingerprint,
    string VisibleMetadataFingerprint,
    string? ContinuationToken = null,
    IReadOnlyDictionary<string, BranchBoundary>? ComponentBoundaries = null)
{
    public static CanonicalState Create(
        IEnumerable<KeyValuePair<string, string>> values,
        string schemaFingerprint,
        string visibleMetadataFingerprint,
        string? continuationToken = null,
        IReadOnlyDictionary<string, BranchBoundary>? componentBoundaries = null)
        => new(
            new SortedDictionary<string, string>(
                values.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            schemaFingerprint,
            visibleMetadataFingerprint,
            continuationToken,
            componentBoundaries);
}

public sealed record ObserverObservation(
    OutcomeClass Outcome,
    CanonicalState? State,
    string? Detail = null);

public sealed record TraceFrame(
    string Operation,
    ObserverObservation Branch,
    ObserverObservation Reference,
    IReadOnlyDictionary<string, ObserverObservation>? BranchObservers = null,
    IReadOnlyDictionary<string, ObserverObservation>? ReferenceObservers = null,
    TraceOperationClass OperationClass = TraceOperationClass.Other);

public sealed record BranchScenario(
    string Name,
    BranchCapabilityProfile Capabilities,
    BranchBoundary DeclaredBoundary,
    CanonicalState BranchAtCreation,
    CanonicalState ReferenceAtCreation,
    IReadOnlyList<TraceFrame> Frames,
    string? ExpectedFailingRelationId = null,
    CreationEvidenceKind CreationEvidence = CreationEvidenceKind.All);

public sealed record RelationResult(
    string RelationId,
    RelationFamily Family,
    RelationStatus Status,
    string Evidence)
{
    public bool Detected => Status == RelationStatus.Fail;
}

public interface IBranchRelation
{
    string Id { get; }

    RelationFamily Family { get; }

    RelationResult Evaluate(BranchScenario scenario);
}

internal static class Comparison
{
    public static bool VisibleValuesEqual(CanonicalState left, CanonicalState right)
    {
        if (left.Values.Count != right.Values.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in left.Values)
        {
            if (!right.Values.TryGetValue(pair.Key, out string? value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool OrdinaryVisibleStateEqual(CanonicalState? left, CanonicalState? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return VisibleValuesEqual(left, right)
            && string.Equals(left.SchemaFingerprint, right.SchemaFingerprint, StringComparison.Ordinal)
            && string.Equals(left.VisibleMetadataFingerprint, right.VisibleMetadataFingerprint, StringComparison.Ordinal);
    }

    public static bool CanonicalStateEqual(CanonicalState? left, CanonicalState? right)
        => OrdinaryVisibleStateEqual(left, right)
            && (left is null
                || string.Equals(left.ContinuationToken, right!.ContinuationToken, StringComparison.Ordinal));
}

public sealed record BaselineResult(string BaselineId, BaselineStatus Status, string Evidence)
{
    public bool Passed => Status is BaselineStatus.Pass or BaselineStatus.NotApplicable or BaselineStatus.Inconclusive;

    public bool Detected => Status == BaselineStatus.Detected;
}

public interface IBranchBaseline
{
    string Id { get; }

    BaselineResult Evaluate(BranchScenario scenario);
}

public sealed class CreationValuesBaseline : IBranchBaseline
{
    public string Id => "B0.creation-values";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        if (!scenario.CreationEvidence.HasFlag(CreationEvidenceKind.Values))
        {
            return new BaselineResult(Id, BaselineStatus.Inconclusive, "Historical evidence does not report complete creation-time values.");
        }

        bool equal = Comparison.VisibleValuesEqual(scenario.BranchAtCreation, scenario.ReferenceAtCreation);
        return new BaselineResult(
            Id,
            equal ? BaselineStatus.Pass : BaselineStatus.Detected,
            equal ? "Visible values are equal at branch creation." : "Visible values differ at branch creation.");
    }
}

public sealed class CreationVisibleStateBaseline : IBranchBaseline
{
    public string Id => "B1.creation-visible-state";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        if ((scenario.CreationEvidence & CreationEvidenceKind.All) != CreationEvidenceKind.All)
        {
            return new BaselineResult(
                Id,
                BaselineStatus.Inconclusive,
                "Historical evidence does not report complete values + schema + visible metadata at creation.");
        }

        bool equal = Comparison.OrdinaryVisibleStateEqual(scenario.BranchAtCreation, scenario.ReferenceAtCreation);
        return new BaselineResult(
            Id,
            equal ? BaselineStatus.Pass : BaselineStatus.Detected,
            equal
                ? "Values, schema fingerprint, and visible metadata are equal at branch creation."
                : "Creation-time values/schema/visible metadata differ.");
    }
}

public sealed class GenericStateDifferentialBaseline : IBranchBaseline
{
    public string Id => "B2.generic-state-differential";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        TraceFrame[] eligible = scenario.Frames
            .Where(static frame => frame.OperationClass is TraceOperationClass.GenericRead or TraceOperationClass.GenericMutation)
            .ToArray();

        if (eligible.Length == 0)
        {
            return new BaselineResult(Id, BaselineStatus.NotApplicable, "No generic read/mutation witness occurs in this recorded trace.");
        }

        foreach (TraceFrame frame in eligible)
        {
            if (frame.Branch.Outcome != frame.Reference.Outcome)
            {
                return new BaselineResult(
                    Id,
                    BaselineStatus.Detected,
                    $"Generic operation '{frame.Operation}' diverged in outcome: branch={frame.Branch.Outcome}, reference={frame.Reference.Outcome}.");
            }

            if (!Comparison.OrdinaryVisibleStateEqual(frame.Branch.State, frame.Reference.State))
            {
                return new BaselineResult(
                    Id,
                    BaselineStatus.Detected,
                    $"Generic operation '{frame.Operation}' diverged in ordinary visible state.");
            }
        }

        return new BaselineResult(Id, BaselineStatus.Pass, $"All {eligible.Length} generic read/mutation witnesses match in ordinary visible state.");
    }
}

public sealed class GenericRecoveryBaseline : IBranchBaseline
{
    public string Id => "B3.generic-recovery";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        TraceFrame[] restartFrames = scenario.Frames
            .Where(static frame => frame.OperationClass == TraceOperationClass.Restart)
            .ToArray();

        if (restartFrames.Length == 0)
        {
            return new BaselineResult(Id, BaselineStatus.NotApplicable, "No restart/recovery witness occurs in this recorded trace.");
        }

        foreach (TraceFrame frame in restartFrames)
        {
            if (frame.Branch.Outcome != frame.Reference.Outcome)
            {
                return new BaselineResult(
                    Id,
                    BaselineStatus.Detected,
                    $"Restart operation '{frame.Operation}' diverged in outcome: branch={frame.Branch.Outcome}, reference={frame.Reference.Outcome}.");
            }

            if (!Comparison.OrdinaryVisibleStateEqual(frame.Branch.State, frame.Reference.State))
            {
                return new BaselineResult(
                    Id,
                    BaselineStatus.Detected,
                    $"Restart operation '{frame.Operation}' diverged in ordinary visible state.");
            }
        }

        return new BaselineResult(Id, BaselineStatus.Pass, "Restart/recovery observations match in ordinary visible state.");
    }
}

public sealed class ContinuationStateRelation : IBranchRelation
{
    public string Id => "BC.continuation-state";

    public RelationFamily Family => RelationFamily.ContinuationState;

    public RelationResult Evaluate(BranchScenario scenario)
    {
        TraceFrame? frame = scenario.Frames.FirstOrDefault(static frame =>
            string.Equals(frame.Operation, "continuation", StringComparison.Ordinal));

        if (frame is null)
        {
            return Result(RelationStatus.Inconclusive, "No continuation witness frame was supplied.");
        }

        if (frame.Branch.Outcome != frame.Reference.Outcome)
        {
            return Result(
                RelationStatus.Fail,
                $"Continuation outcome diverged: branch={frame.Branch.Outcome}, reference={frame.Reference.Outcome}.");
        }

        if (frame.Branch.State?.ContinuationToken is null || frame.Reference.State?.ContinuationToken is null)
        {
            return Result(RelationStatus.Inconclusive, "Continuation witness did not expose canonical continuation tokens.");
        }

        bool equal = string.Equals(
            frame.Branch.State.ContinuationToken,
            frame.Reference.State.ContinuationToken,
            StringComparison.Ordinal);

        return Result(
            equal ? RelationStatus.Pass : RelationStatus.Fail,
            equal
                ? $"Continuation token preserved ({frame.Branch.State.ContinuationToken})."
                : $"Continuation token diverged: branch={frame.Branch.State.ContinuationToken}, reference={frame.Reference.State.ContinuationToken}.");
    }

    private RelationResult Result(RelationStatus status, string evidence)
        => new(Id, Family, status, evidence);
}

public sealed class TemporalBoundaryRelation : IBranchRelation
{
    public string Id => "BC.temporal-boundary";

    public RelationFamily Family => RelationFamily.TemporalBoundary;

    public RelationResult Evaluate(BranchScenario scenario)
    {
        if (!scenario.Capabilities.SupportsHistoricalFork)
        {
            return Result(RelationStatus.NotApplicable, "Backend does not advertise historical fork capability.");
        }

        if (scenario.Capabilities.SourceBoundaryComponents.Count == 0)
        {
            return Result(RelationStatus.Inconclusive, "Capability profile declares no component that must preserve the source boundary.");
        }

        IReadOnlyDictionary<string, BranchBoundary>? boundaries = scenario.BranchAtCreation.ComponentBoundaries;
        if (boundaries is null)
        {
            return Result(RelationStatus.Inconclusive, "Adapter did not expose component-boundary evidence.");
        }

        foreach (string component in scenario.Capabilities.SourceBoundaryComponents.Order(StringComparer.Ordinal))
        {
            if (!boundaries.TryGetValue(component, out BranchBoundary observed))
            {
                return Result(RelationStatus.Inconclusive, $"Missing boundary evidence for '{component}'.");
            }

            if (observed != scenario.DeclaredBoundary)
            {
                return Result(
                    RelationStatus.Fail,
                    $"Component '{component}' came from {observed}; declared branch boundary is {scenario.DeclaredBoundary}.");
            }
        }

        return Result(
            RelationStatus.Pass,
            $"All profile-declared source-boundary components resolve to {scenario.DeclaredBoundary}.");
    }

    private RelationResult Result(RelationStatus status, string evidence)
        => new(Id, Family, status, evidence);
}

public sealed class LifecycleRelation : IBranchRelation
{
    public string Id => "BC.lifecycle";

    public RelationFamily Family => RelationFamily.Lifecycle;

    public RelationResult Evaluate(BranchScenario scenario)
    {
        if (!scenario.Capabilities.SupportsDelete)
        {
            return Result(RelationStatus.NotApplicable, "Backend does not advertise branch deletion capability.");
        }

        TraceFrame? frame = scenario.Frames.FirstOrDefault(static frame =>
            string.Equals(frame.Operation, "delete-branch", StringComparison.Ordinal));
        if (frame is null)
        {
            return Result(RelationStatus.Inconclusive, "No delete-branch witness frame was supplied.");
        }

        bool equal = frame.Branch.Outcome == frame.Reference.Outcome;
        return Result(
            equal ? RelationStatus.Pass : RelationStatus.Fail,
            equal
                ? $"Lifecycle outcome matches reference ({frame.Branch.Outcome})."
                : $"Lifecycle outcome diverged: branch={frame.Branch.Outcome}, reference={frame.Reference.Outcome}.");
    }

    private RelationResult Result(RelationStatus status, string evidence)
        => new(Id, Family, status, evidence);
}

public sealed class ObserverDependencyRelation : IBranchRelation
{
    public string Id => "BC.observer-dependency";

    public RelationFamily Family => RelationFamily.ObserverDependency;

    public RelationResult Evaluate(BranchScenario scenario)
    {
        if (scenario.Capabilities.EquivalentObservers.Count == 0)
        {
            return Result(RelationStatus.NotApplicable, "Backend declares no observer-equivalence set.");
        }

        TraceFrame? frame = scenario.Frames.FirstOrDefault(static frame =>
            string.Equals(frame.Operation, "observe", StringComparison.Ordinal));
        if (frame?.BranchObservers is null || frame.ReferenceObservers is null)
        {
            return Result(RelationStatus.Inconclusive, "Observer witness data was not supplied.");
        }

        foreach (string observer in scenario.Capabilities.EquivalentObservers.Order(StringComparer.Ordinal))
        {
            if (!frame.BranchObservers.TryGetValue(observer, out ObserverObservation? branchObservation)
                || !frame.ReferenceObservers.TryGetValue(observer, out ObserverObservation? referenceObservation))
            {
                return Result(RelationStatus.Inconclusive, $"Missing observer evidence for '{observer}'.");
            }

            if (branchObservation.Outcome != referenceObservation.Outcome)
            {
                return Result(
                    RelationStatus.Fail,
                    $"Observer '{observer}' outcome diverged: branch={branchObservation.Outcome}, reference={referenceObservation.Outcome}.");
            }

            if (!Comparison.CanonicalStateEqual(branchObservation.State, referenceObservation.State))
            {
                return Result(RelationStatus.Fail, $"Observer '{observer}' canonical state diverged from reference.");
            }
        }

        return Result(RelationStatus.Pass, "All declared equivalent observers match their reference observations.");
    }

    private RelationResult Result(RelationStatus status, string evidence)
        => new(Id, Family, status, evidence);
}

public sealed class RecoveryClosureRelation : IBranchRelation
{
    public string Id => "BC.recovery";

    public RelationFamily Family => RelationFamily.Recovery;

    public RelationResult Evaluate(BranchScenario scenario)
    {
        if (!scenario.Capabilities.SupportsRestart)
        {
            return Result(RelationStatus.NotApplicable, "Backend does not advertise restart/recovery capability.");
        }

        TraceFrame? frame = scenario.Frames.FirstOrDefault(static frame => frame.OperationClass == TraceOperationClass.Restart);
        if (frame is null)
        {
            return Result(RelationStatus.Inconclusive, "No restart/recovery witness frame was supplied.");
        }

        if (frame.Branch.Outcome != frame.Reference.Outcome)
        {
            return Result(
                RelationStatus.Fail,
                $"Recovery outcome diverged: branch={frame.Branch.Outcome}, reference={frame.Reference.Outcome}.");
        }

        bool equal = Comparison.CanonicalStateEqual(frame.Branch.State, frame.Reference.State);
        return Result(
            equal ? RelationStatus.Pass : RelationStatus.Fail,
            equal ? "Recovered branch observation matches the reference world." : "Recovered branch state diverged from the reference world.");
    }

    private RelationResult Result(RelationStatus status, string evidence)
        => new(Id, Family, status, evidence);
}

public sealed record ScenarioReport(
    string Name,
    IReadOnlyList<BaselineResult> Baselines,
    IReadOnlyList<RelationResult> Relations)
{
    public bool BranchCheckDetected => Relations.Any(static result => result.Detected);

    public bool GenericBaselineDetected => Baselines.Any(static result =>
        result.BaselineId is "B2.generic-state-differential" or "B3.generic-recovery"
        && result.Detected);

    public bool BranchCheckOnly => BranchCheckDetected && !GenericBaselineDetected;
}

public static class BranchCheckRunner
{
    public static IReadOnlyList<IBranchBaseline> DefaultBaselines { get; } =
    [
        new CreationValuesBaseline(),
        new CreationVisibleStateBaseline(),
        new GenericStateDifferentialBaseline(),
        new GenericRecoveryBaseline(),
    ];

    public static IReadOnlyList<IBranchRelation> DefaultRelations { get; } =
    [
        new ContinuationStateRelation(),
        new TemporalBoundaryRelation(),
        new LifecycleRelation(),
        new ObserverDependencyRelation(),
        new RecoveryClosureRelation(),
    ];

    public static ScenarioReport Evaluate(BranchScenario scenario)
        => new(
            scenario.Name,
            DefaultBaselines.Select(baseline => baseline.Evaluate(scenario)).ToArray(),
            DefaultRelations.Select(relation => relation.Evaluate(scenario)).ToArray());
}
