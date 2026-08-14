namespace ChronicleDB.BranchCheck;

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
            CreateRecoveryMutation(),
        ];

    private static BranchScenario CreateCleanScenario()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "clean-control",
            BranchCapabilityProfile.Create(
                "synthetic",
                supportsHistoricalFork: true,
                supportsRestart: true,
                supportsDelete: true,
                equivalentObservers: ["primary", "secondary"],
                sourceBoundaryComponents: ["data", "metadata", "dependencies", "continuation"]),
            Boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "continuation",
                    Success(StateWithToken("4")),
                    Success(StateWithToken("4")),
                    OperationClass: TraceOperationClass.ContinuationProbe),
                new TraceFrame(
                    "delete-branch",
                    Success(null),
                    Success(null),
                    OperationClass: TraceOperationClass.BranchSpecificLifecycle),
                ObserverFrame(secondaryFails: false),
                new TraceFrame(
                    "restart",
                    Success(creation),
                    Success(creation),
                    OperationClass: TraceOperationClass.Restart),
            ]);
    }

    private static BranchScenario CreateContinuationMutation()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "mutation-continuation",
            BranchCapabilityProfile.Create("synthetic"),
            Boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "continuation",
                    Success(StateWithToken("10001")),
                    Success(StateWithToken("4")),
                    OperationClass: TraceOperationClass.ContinuationProbe),
            ],
            "BC.continuation-state");
    }

    private static BranchScenario CreateBoundaryMutation()
    {
        Dictionary<string, BranchBoundary> boundaries = AllBoundaries(Boundary);
        boundaries["metadata"] = new BranchBoundary("main", 101);
        return new BranchScenario(
            "mutation-temporal-boundary",
            BranchCapabilityProfile.Create(
                "synthetic",
                supportsHistoricalFork: true,
                sourceBoundaryComponents: ["data", "metadata", "dependencies", "continuation"]),
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
            [
                new TraceFrame(
                    "delete-branch",
                    new ObserverObservation(OutcomeClass.Rejected, null, "branch cannot be deleted"),
                    Success(null),
                    OperationClass: TraceOperationClass.BranchSpecificLifecycle),
            ],
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

    private static BranchScenario CreateRecoveryMutation()
    {
        CanonicalState creation = CreationState(AllBoundaries(Boundary));
        return new BranchScenario(
            "mutation-recovery",
            BranchCapabilityProfile.Create("synthetic", supportsRestart: true),
            Boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "restart",
                    new ObserverObservation(OutcomeClass.Crash, null, "latent recovery state became active"),
                    Success(creation),
                    OperationClass: TraceOperationClass.Restart),
            ],
            "BC.recovery");
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
        return new TraceFrame(
            "observe",
            Success(state),
            Success(state),
            branchObservers,
            referenceObservers,
            TraceOperationClass.ObserverRead);
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
