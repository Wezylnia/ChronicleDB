#if CHRONICLEDB_ENGINE_AVAILABLE
using EngineBranch = global::ChronicleDB.ChronicleBranch;
using EngineBranchSnapshot = global::ChronicleDB.ChronicleBranchSnapshot;
using EngineBranchHistoricalView = global::ChronicleDB.ChronicleBranchHistoricalView;
using EngineDatabase = global::ChronicleDB.ChronicleDatabase;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class ChronicleDbAdapterIntegrationTests
{
    [Fact]
    public void RealHistoricalBranchPassesCapabilityAwareFutureTraceRelations()
    {
        BranchScenario scenario = ChronicleDbScenarioAdapter.ExecuteHistoricalForkRoundTrip();
        ScenarioReport report = BranchCheckRunner.Evaluate(scenario);

        Assert.Equal(BaselineStatus.Pass, FindBaseline(report, "B0.creation-values").Status);
        Assert.Equal(BaselineStatus.Pass, FindBaseline(report, "B1.creation-visible-state").Status);
        Assert.Equal(RelationStatus.Pass, FindRelation(report, "BC.continuation-state").Status);
        Assert.Equal(RelationStatus.Pass, FindRelation(report, "BC.temporal-boundary").Status);
        Assert.Equal(RelationStatus.Pass, FindRelation(report, "BC.lifecycle").Status);
        Assert.Equal(RelationStatus.Pass, FindRelation(report, "BC.observer-dependency").Status);
        Assert.Equal(RelationStatus.Pass, FindRelation(report, "BC.recovery").Status);
        Assert.False(report.BranchCheckDetected);
    }

    private static BaselineResult FindBaseline(ScenarioReport report, string id)
        => Assert.Single(report.Baselines, result => string.Equals(result.BaselineId, id, StringComparison.Ordinal));

    private static RelationResult FindRelation(ScenarioReport report, string id)
        => Assert.Single(report.Relations, result => string.Equals(result.RelationId, id, StringComparison.Ordinal));
}

internal static class ChronicleDbScenarioAdapter
{
    private static readonly byte[] BaseKey = [0x01];
    private static readonly byte[] LocalKey = [0x02];
    private static readonly byte[] OldValue = [0x10];
    private static readonly byte[] NewParentValue = [0x20];
    private static readonly byte[] LocalValue = [0x30];
    private static readonly byte[][] Keys = [BaseKey, LocalKey];

    public static BranchScenario ExecuteHistoricalForkRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "chronicle-branchcheck-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "source");
        string referenceDirectory = Path.Combine(root, "reference");
        Directory.CreateDirectory(root);

        try
        {
            Guid branchId;
            BranchBoundary declaredBoundary;
            CanonicalState creationBranch;
            CanonicalState creationReference;
            var frames = new List<TraceFrame>();

            using (var source = EngineDatabase.Open(sourceDirectory))
            using (var reference = EngineDatabase.Open(referenceDirectory))
            {
                source.Put(BaseKey, OldValue);
                ulong sourceBoundary = source.CurrentCommitSequence.Value;
                source.Put(BaseKey, NewParentValue);
                reference.Put(BaseKey, OldValue);

                using EngineBranch branch = source.CreateBranch("branchcheck-adapter", sourceBoundary);
                branchId = branch.BranchId;
                declaredBoundary = new BranchBoundary(
                    branch.Info.ParentHistoryId.ToString("N"),
                    checked((long)branch.Info.ParentBaseSequence));

                Assert.Equal(sourceBoundary, branch.Info.ParentBaseSequence);
                Assert.True(branch.TryGet(BaseKey, out byte[] inheritedAtCreation));
                Assert.Equal(OldValue, inheritedAtCreation);

                Dictionary<string, BranchBoundary> boundaryEvidence = new(StringComparer.Ordinal)
                {
                    ["data"] = declaredBoundary,
                    ["metadata"] = declaredBoundary,
                    ["dependencies"] = declaredBoundary,
                };
                creationBranch = ObserveBranch(branch, boundaryEvidence);
                creationReference = ObserveDatabase(reference);

                source.Put(BaseKey, [0x21]);
                branch.Put(LocalKey, LocalValue);
                reference.Put(LocalKey, LocalValue);

                CanonicalState branchAfterContinuation = ObserveBranch(branch, boundaryEvidence, includeContinuationToken: true);
                CanonicalState referenceAfterContinuation = ObserveDatabase(reference, includeContinuationToken: true);
                frames.Add(new TraceFrame(
                    "continuation",
                    Success(branchAfterContinuation),
                    Success(referenceAfterContinuation),
                    OperationClass: TraceOperationClass.GenericMutation));

                ulong observerSequence = branch.CurrentSequence;
                using (EngineBranchSnapshot snapshot = branch.CreateSnapshot("branchcheck-observer"))
                using (EngineBranchHistoricalView historical = branch.OpenHistoricalView(observerSequence))
                {
                    CanonicalState currentState = ObserveBranch(branch, boundaryEvidence, includeContinuationToken: true);
                    CanonicalState snapshotState = ObserveSnapshot(snapshot, includeContinuationToken: true);
                    CanonicalState historicalState = ObserveHistorical(historical, includeContinuationToken: true);
                    CanonicalState referenceState = ObserveDatabase(reference, includeContinuationToken: true);
                    var branchObservers = new Dictionary<string, ObserverObservation>(StringComparer.Ordinal)
                    {
                        ["current"] = Success(currentState),
                        ["snapshot"] = Success(snapshotState),
                        ["historical"] = Success(historicalState),
                    };
                    var referenceObservers = new Dictionary<string, ObserverObservation>(StringComparer.Ordinal)
                    {
                        ["current"] = Success(referenceState),
                        ["snapshot"] = Success(referenceState),
                        ["historical"] = Success(referenceState),
                    };
                    frames.Add(new TraceFrame(
                        "observe",
                        Success(currentState),
                        Success(referenceState),
                        branchObservers,
                        referenceObservers,
                        TraceOperationClass.ObserverRead));

                    Guid snapshotId = snapshot.Info.SnapshotId;
                    snapshot.Dispose();
                    branch.DeleteSnapshot(snapshotId);
                }
            }

            using (var reopenedSource = EngineDatabase.Open(sourceDirectory))
            using (var reopenedReference = EngineDatabase.Open(referenceDirectory))
            {
                using (EngineBranch reopenedBranch = reopenedSource.OpenBranch(branchId))
                {
                    CanonicalState recoveredBranch = ObserveBranch(reopenedBranch, componentBoundaries: null, includeContinuationToken: true);
                    CanonicalState recoveredReference = ObserveDatabase(reopenedReference, includeContinuationToken: true);
                    frames.Add(new TraceFrame(
                        "restart",
                        Success(recoveredBranch),
                        Success(recoveredReference),
                        OperationClass: TraceOperationClass.Restart));
                }

                OutcomeClass deleteOutcome;
                string? deleteDetail = null;
                try
                {
                    reopenedSource.DeleteBranch(branchId);
                    deleteOutcome = OutcomeClass.Success;
                }
                catch (Exception exception)
                {
                    deleteOutcome = OutcomeClass.Rejected;
                    deleteDetail = exception.GetType().Name + ": " + exception.Message;
                }

                frames.Add(new TraceFrame(
                    "delete-branch",
                    new ObserverObservation(deleteOutcome, null, deleteDetail),
                    Success(null),
                    OperationClass: TraceOperationClass.BranchSpecificLifecycle));
            }

            return new BranchScenario(
                "chronicledb-real-historical-roundtrip",
                BranchCapabilityProfile.Create(
                    "ChronicleDB",
                    supportsHistoricalFork: true,
                    supportsRestart: true,
                    supportsDelete: true,
                    equivalentObservers: ["current", "snapshot", "historical"],
                    sourceBoundaryComponents: ["data", "metadata", "dependencies"]),
                declaredBoundary,
                creationBranch,
                creationReference,
                frames,
                CreationEvidence: CreationEvidenceKind.All);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static CanonicalState ObserveDatabase(EngineDatabase database, bool includeContinuationToken = false)
        => Observe(
            key => database.TryGet(key, out byte[] value) ? value : null,
            includeContinuationToken,
            componentBoundaries: null);

    private static CanonicalState ObserveBranch(
        EngineBranch branch,
        IReadOnlyDictionary<string, BranchBoundary>? componentBoundaries,
        bool includeContinuationToken = false)
        => Observe(
            key => branch.TryGet(key, out byte[] value) ? value : null,
            includeContinuationToken,
            componentBoundaries);

    private static CanonicalState ObserveSnapshot(EngineBranchSnapshot snapshot, bool includeContinuationToken)
        => Observe(
            key => snapshot.TryGet(key, out byte[] value) ? value : null,
            includeContinuationToken,
            componentBoundaries: null);

    private static CanonicalState ObserveHistorical(EngineBranchHistoricalView historical, bool includeContinuationToken)
        => Observe(
            key => historical.TryGet(key, out byte[] value) ? value : null,
            includeContinuationToken,
            componentBoundaries: null);

    private static CanonicalState Observe(
        Func<byte[], byte[]?> reader,
        bool includeContinuationToken,
        IReadOnlyDictionary<string, BranchBoundary>? componentBoundaries)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (byte[] key in Keys)
        {
            byte[]? value = reader(key);
            values[Convert.ToHexString(key)] = value is null ? "<missing>" : Convert.ToHexString(value);
        }

        string token = string.Join(
            ";",
            values.Select(static pair => pair.Key + "=" + pair.Value));
        return CanonicalState.Create(
            values,
            "binary-kv-v1",
            "canonical-visible-kv-state",
            includeContinuationToken ? token : null,
            componentBoundaries);
    }

    private static ObserverObservation Success(CanonicalState? state)
        => new(OutcomeClass.Success, state);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
#endif
