using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class PersistenceTraceProjectionTests
{
    private static readonly HistoryId Main = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly HistoryId HistoryA = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly HistoryId HistoryB = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));

    [Fact]
    public void CompleteRealTraceOperationsPreserveSharedCatalogConstraintAndPorEquivalence()
    {
        var events = Operation(1, HistoryA, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "a")
            .Concat(Operation(5, HistoryB, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "b"))
            .ToArray();
        ResearchTraceValidator.Validate(events);

        var actions = PersistenceTraceSlice.SelectCompleteOperations(events, 2);
        var oracle = new PersistenceTraceProjectionOracle(actions);
        var explorer = new BoundedCrashPorExplorer(actions);
        var result = explorer.VerifyCrashPrefixEquivalence(oracle.Evaluate);

        Assert.Equal(8, actions.Count);
        Assert.Contains("branch-catalog", oracle.SharedResources);
        Assert.True(result.ObservationSetsEquivalent);
        Assert.True(result.CrashPlanReductionFactor > 1d);
        Assert.True(result.ReducedOrderCount > 1);
    }

    [Fact]
    public void ProjectionRejectsDependencyOutsideSelectedSlice()
    {
        var events = Operation(2, HistoryA, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "a")
            .ToArray();
        events[0] = Event(
            2,
            ResearchEventKind.OperationStarted,
            HistoryA,
            events[0].OperationId,
            ["a-data", "a-wal"],
            ResearchDurabilityPhase.Prepared,
            dependencies: [1]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => PersistenceTraceSlice.SelectCompleteOperations(events, 1));
        Assert.Contains("omit dependency", exception.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void CatalogBlindReductionIsRejectedByRealSharedResourceObserver()
    {
        var actions = PersistenceTraceSlice.SelectCompleteOperations(
            Operation(1, HistoryA, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "a")
                .Concat(Operation(5, HistoryB, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "b")),
            2);
        var oracle = new PersistenceTraceProjectionOracle(actions);
        var catalogBlind = actions
            .Select(action => new PersistenceAction(
                action.ActionId,
                action.EventKind,
                action.HistoryId,
                action.ParentHistoryId,
                action.OperationId,
                action.ResourceSet.Where(resource => !resource.Equals("branch-catalog", StringComparison.Ordinal)),
                action.DurabilityPhase,
                action.AuthorityGeneration,
                action.DependencyActionIds))
            .ToArray();
        var realById = actions.ToDictionary(action => action.ActionId);
        var explorer = new BoundedCrashPorExplorer(catalogBlind);

        var result = explorer.VerifyCrashPrefixEquivalence(
            prefix => oracle.Evaluate(prefix.Select(action => realById[action.ActionId]).ToArray()));

        Assert.False(result.ObservationSetsEquivalent);
    }

    [Fact]
    public void SharedResourceOrderRemainsObservable()
    {
        var actions = PersistenceTraceSlice.SelectCompleteOperations(
            Operation(1, HistoryA, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "a")
                .Concat(Operation(5, HistoryB, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "b")),
            2);
        var oracle = new PersistenceTraceProjectionOracle(actions);
        var authorityA = actions.Single(action => action.HistoryId == HistoryA && action.EventKind == ResearchEventKind.AuthorityPublished);
        var authorityB = actions.Single(action => action.HistoryId == HistoryB && action.EventKind == ResearchEventKind.AuthorityPublished);

        Assert.False(oracle.Evaluate([authorityA, authorityB]).EquivalentTo(oracle.Evaluate([authorityB, authorityA])));
    }

    private static IEnumerable<ResearchEvent> Operation(
        long firstId,
        HistoryId history,
        Guid operation,
        string prefix)
    {
        yield return Event(
            firstId,
            ResearchEventKind.OperationStarted,
            history,
            operation,
            [$"{prefix}-data", $"{prefix}-wal"],
            ResearchDurabilityPhase.Prepared);
        yield return Event(
            firstId + 1,
            ResearchEventKind.DurabilityBarrier,
            history,
            operation,
            [$"{prefix}-data", $"{prefix}-wal"],
            ResearchDurabilityPhase.StableStorageBarrier,
            [firstId]);
        yield return Event(
            firstId + 2,
            ResearchEventKind.AuthorityPublished,
            history,
            operation,
            [$"{prefix}-data", $"{prefix}-wal", "branch-catalog"],
            ResearchDurabilityPhase.AuthorityPublished,
            [firstId + 1]);
        yield return Event(
            firstId + 3,
            ResearchEventKind.OperationCompleted,
            history,
            operation,
            [$"{prefix}-data", $"{prefix}-wal", "branch-catalog"],
            ResearchDurabilityPhase.AuthorityPublished,
            [firstId + 2]);
    }

    private static ResearchEvent Event(
        long id,
        ResearchEventKind kind,
        HistoryId history,
        Guid operation,
        IEnumerable<string> resources,
        ResearchDurabilityPhase phase,
        IEnumerable<long>? dependencies = null)
        => new(
            id,
            id,
            kind,
            history,
            Main,
            operation,
            operation,
            resources,
            phase,
            authorityGeneration: 1,
            dependencies ?? [],
            logicalKeyId: null,
            versionId: null,
            offset: null,
            bytes: null);
}
