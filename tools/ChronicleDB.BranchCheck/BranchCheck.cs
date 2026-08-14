namespace ChronicleDB.BranchCheck;

public enum RelationFamily
{
    ContinuationState,
    TemporalBoundary,
    Lifecycle,
    ObserverDependency,
}

public enum RelationStatus
{
    Pass,
    Fail,
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

public readonly record struct BranchBoundary(string HistoryId, long Sequence)
{
    public override string ToString() => $"{HistoryId}@{Sequence}";
}

public sealed record BranchCapabilityProfile(
    string BackendName,
    bool SupportsHistoricalFork,
    bool SupportsRestart,
    bool SupportsDelete,
    IReadOnlySet<string> EquivalentObservers)
{
    public static BranchCapabilityProfile Create(
        string backendName,
        bool supportsHistoricalFork = false,
        bool supportsRestart = false,
        bool supportsDelete = false,
        params string[] equivalentObservers)
        => new(
            backendName,
            supportsHistoricalFork,
            supportsRestart,
            supportsDelete,
            new HashSet<string>(equivalentObservers, StringComparer.Ordinal));
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
            new SortedDictionary<string, string>(values.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal),
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
    IReadOnlyDictionary<string, ObserverObservation>? ReferenceObservers = null);

public sealed record BranchScenario(
    string Name,
    BranchCapabilityProfile Capabilities,
    BranchBoundary DeclaredBoundary,
    CanonicalState BranchAtCreation,
    CanonicalState ReferenceAtCreation,
    IReadOnlyList<TraceFrame> Frames,
    string? ExpectedFailingRelationId = null);

public sealed record RelationResult(
    string RelationId,
    RelationFamily Family,
    RelationStatus Status,
    string Evidence);

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

    public static bool CreationVisibleStateEqual(CanonicalState left, CanonicalState right)
        => VisibleValuesEqual(left, right)
            && string.Equals(left.SchemaFingerprint, right.SchemaFingerprint, StringComparison.Ordinal)
            && string.Equals(left.VisibleMetadataFingerprint, right.VisibleMetadataFingerprint, StringComparison.Ordinal);

    public static bool CanonicalStateEqual(CanonicalState? left, CanonicalState? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return CreationVisibleStateEqual(left, right)
            && string.Equals(left.ContinuationToken, right.ContinuationToken, StringComparison.Ordinal);
    }
}

public sealed record BaselineResult(string BaselineId, bool Passed, string Evidence);

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
        bool passed = Comparison.VisibleValuesEqual(scenario.BranchAtCreation, scenario.ReferenceAtCreation);
        return new BaselineResult(
            Id,
            passed,
            passed
                ? "Visible values are equal at branch creation."
                : "Visible values differ at branch creation.");
    }
}

public sealed class CreationVisibleStateBaseline : IBranchBaseline
{
    public string Id => "B1.creation-visible-state";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        bool passed = Comparison.CreationVisibleStateEqual(scenario.BranchAtCreation, scenario.ReferenceAtCreation);
        return new BaselineResult(
            Id,
            passed,
            passed
                ? "Values, schema fingerprint, and visible metadata are equal at branch creation."
                : "Creation-time values/schema/visible metadata differ.");
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
    private static readonly string[] RequiredComponents = ["data", "metadata", "dependencies", "continuation"];

    public string Id => "BC.temporal-boundary";

    public RelationFamily Family => RelationFamily.TemporalBoundary;

    public RelationResult Evaluate(BranchScenario scenario)
    {
        if (!scenario.Capabilities.SupportsHistoricalFork)
        {
            return Result(RelationStatus.NotApplicable, "Backend does not advertise historical fork capability.");
        }

        IReadOnlyDictionary<string, BranchBoundary>? boundaries = scenario.BranchAtCreation.ComponentBoundaries;
        if (boundaries is null)
        {
            return Result(RelationStatus.Inconclusive, "Adapter did not expose component-boundary evidence.");
        }

        foreach (string component in RequiredComponents)
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

        return Result(RelationStatus.Pass, $"All required components resolve to {scenario.DeclaredBoundary}.");
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

public static class SyntheticCampaign
{
    private static readonly BranchBoundary Boundary = new("main", 100);

    public static IReadOnlyList<BranchScenario> Create()
        =>
        [
            CreateCleanScenario(),
            CreateContinuationMutation(),
            CreateBoundaryMutation(),
            CreateLifecycleMutation(),
            CreateObserverMutation(),
        ];

    private static BranchScenario CreateCleanScenario()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "clean-control",
            BranchCapabilityProfile.Create("synthetic", true, true, true, "primary", "secondary"),
            Boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "continuation",
                    Success(StateWithToken("4")),
                    Success(StateWithToken("4"))),
                new TraceFrame("delete-branch", Success(null), Success(null)),
                ObserverFrame(secondaryFails: false),
            ]);
    }

    private static BranchScenario CreateContinuationMutation()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "mutation-continuation",
            BranchCapabilityProfile.Create("synthetic", supportsDelete: true),
            Boundary,
            creation,
            creation,
            [new TraceFrame("continuation", Success(StateWithToken("10001")), Success(StateWithToken("4")))],
            "BC.continuation-state");
    }

    private static BranchScenario CreateBoundaryMutation()
    {
        Dictionary<string, BranchBoundary> boundaries = AllBoundaries(Boundary);
        boundaries["metadata"] = new BranchBoundary("main", 101);
        return new BranchScenario(
            "mutation-temporal-boundary",
            BranchCapabilityProfile.Create("synthetic", supportsHistoricalFork: true),
            Boundary,
            CreationState(boundaries),
            CreationState(AllBoundaries(Boundary)),
            [],
            "BC.temporal-boundary");
    }

    private static BranchScenario CreateLifecycleMutation()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "mutation-lifecycle",
            BranchCapabilityProfile.Create("synthetic", supportsDelete: true),
            Boundary,
            creation,
            creation,
            [new TraceFrame("delete-branch", new ObserverObservation(OutcomeClass.Rejected, null, "branch cannot be deleted"), Success(null))],
            "BC.lifecycle");
    }

    private static BranchScenario CreateObserverMutation()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "mutation-observer-dependency",
            BranchCapabilityProfile.Create("synthetic", equivalentObservers: ["primary", "secondary"]),
            Boundary,
            creation,
            creation,
            [ObserverFrame(secondaryFails: true)],
            "BC.observer-dependency");
    }

    private static TraceFrame ObserverFrame(bool secondaryFails)
    {
        CanonicalState state = CreationState(AllBoundaries(Boundary));
        Dictionary<string, ObserverObservation> branchObservers = new(StringComparer.Ordinal)
        {
            ["primary"] = Success(state),
            ["secondary"] = secondaryFails
                ? new ObserverObservation(OutcomeClass.NotFound, null, "parent dependency was not resolved")
                : Success(state),
        };
        Dictionary<string, ObserverObservation> referenceObservers = new(StringComparer.Ordinal)
        {
            ["primary"] = Success(state),
            ["secondary"] = Success(state),
        };
        return new TraceFrame("observe", Success(state), Success(state), branchObservers, referenceObservers);
    }

    private static CanonicalState CreationState(IReadOnlyDictionary<string, BranchBoundary> boundaries)
        => CanonicalState.Create(
            [new KeyValuePair<string, string>("account:42", "500")],
            "schema-v1",
            "metadata-v1",
            componentBoundaries: boundaries);

    private static CanonicalState StateWithToken(string token)
        => CanonicalState.Create(
            [new KeyValuePair<string, string>("account:42", "500")],
            "schema-v1",
            "metadata-v1",
            continuationToken: token,
            componentBoundaries: AllBoundaries(Boundary));

    private static Dictionary<string, BranchBoundary> AllBoundaries(BranchBoundary boundary)
        => new(StringComparer.Ordinal)
        {
            ["data"] = boundary,
            ["metadata"] = boundary,
            ["dependencies"] = boundary,
            ["continuation"] = boundary,
        };

    private static ObserverObservation Success(CanonicalState? state)
        => new(OutcomeClass.Success, state);
}
