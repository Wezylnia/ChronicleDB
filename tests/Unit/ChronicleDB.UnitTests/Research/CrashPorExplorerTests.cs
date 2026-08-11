using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class CrashPorExplorerTests
{
    private static readonly HistoryId HistoryA = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly HistoryId HistoryB = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));

    [Fact]
    public void IndependentHistoriesReduceOrderPermutationsButPreserveDistinctCrashSubsets()
    {
        var explorer = new BoundedCrashPorExplorer(
        [
            Action(1, HistoryA, "a.wal"),
            Action(2, HistoryB, "b.wal"),
        ]);

        var exhaustiveOrders = explorer.EnumerateExhaustiveOrders();
        var reducedOrders = explorer.EnumerateReducedOrders();
        var reducedCrashPlans = explorer.EnumerateReducedCrashPrefixes();

        Assert.Equal(2, exhaustiveOrders.Count);
        Assert.Single(reducedOrders);
        Assert.Equal(4, reducedCrashPlans.Count);
        Assert.Contains(reducedCrashPlans, plan => plan.Count == 1 && plan[0].ActionId == 1);
        Assert.Contains(reducedCrashPlans, plan => plan.Count == 1 && plan[0].ActionId == 2);
    }

    [Fact]
    public void SoundIndependencePreservesCanonicalCrashObservationSet()
    {
        var explorer = new BoundedCrashPorExplorer(
        [
            Action(1, HistoryA, "a.wal"),
            Action(2, HistoryB, "b.wal"),
            Action(3, new HistoryId(Guid.Parse("30000000-0000-0000-0000-000000000001")), "c.wal"),
        ]);

        var result = explorer.VerifyCrashPrefixEquivalence(OrderInsensitiveEvaluator);

        Assert.True(result.ObservationSetsEquivalent);
        Assert.Equal(6, result.ExhaustiveOrderCount);
        Assert.Equal(1, result.ReducedOrderCount);
        Assert.True(result.CrashPlanReductionFactor > 1d);
    }

    [Fact]
    public void VerificationDetectsFalseIndependenceWhenObserverCanSeeOrder()
    {
        var explorer = new BoundedCrashPorExplorer(
        [
            Action(1, HistoryA, "a.wal"),
            Action(2, HistoryB, "b.wal"),
        ]);

        var result = explorer.VerifyCrashPrefixEquivalence(OrderSensitiveEvaluator);

        Assert.False(result.ObservationSetsEquivalent);
        Assert.True(result.ExhaustiveObservationTraceCount > result.ReducedObservationTraceCount);
    }

    [Fact]
    public void SharedResourceAndAncestryPreventIndependenceMerging()
    {
        var sharedExplorer = new BoundedCrashPorExplorer(
        [
            Action(1, HistoryA, "catalog"),
            Action(2, HistoryB, "catalog"),
        ]);
        Assert.Equal(2, sharedExplorer.EnumerateReducedOrders().Count);

        var child = new PersistenceAction(
            2,
            ResearchEventKind.OperationStarted,
            HistoryB,
            HistoryA,
            Guid.NewGuid(),
            ["b.wal"],
            ResearchDurabilityPhase.WalAppended,
            1);
        var ancestryExplorer = new BoundedCrashPorExplorer(
        [
            Action(1, HistoryA, "a.wal"),
            child,
        ]);
        Assert.Equal(2, ancestryExplorer.EnumerateReducedOrders().Count);
    }

    [Fact]
    public void DisjointPerHistoryAuthorityPublicationCanCommute()
    {
        var explorer = new BoundedCrashPorExplorer(
        [
            new PersistenceAction(
                1,
                ResearchEventKind.AuthorityPublished,
                HistoryA,
                null,
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                ["a.wal", "a.checkpoint"],
                ResearchDurabilityPhase.AuthorityPublished,
                7),
            new PersistenceAction(
                2,
                ResearchEventKind.AuthorityPublished,
                HistoryB,
                null,
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                ["b.wal", "b.checkpoint"],
                ResearchDurabilityPhase.AuthorityPublished,
                11),
        ]);

        Assert.Single(explorer.EnumerateReducedOrders());
    }

    [Fact]
    public void SameHistoryProgramOrderIsNeverPermuted()
    {
        var explorer = new BoundedCrashPorExplorer(
        [
            Action(1, HistoryA, "a-1"),
            Action(2, HistoryA, "a-2"),
            Action(3, HistoryB, "b"),
        ]);

        Assert.All(
            explorer.EnumerateExhaustiveOrders(),
            order => Assert.True(
                Array.FindIndex(order.ToArray(), action => action.ActionId == 1)
                < Array.FindIndex(order.ToArray(), action => action.ActionId == 2)));
    }

    private static PersistenceAction Action(long id, HistoryId historyId, string resource)
        => new(
            id,
            ResearchEventKind.OperationStarted,
            historyId,
            null,
            Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
            [resource],
            ResearchDurabilityPhase.WalAppended,
            1);

    private static CanonicalObservationTrace OrderInsensitiveEvaluator(IReadOnlyList<PersistenceAction> actions)
        => Trace(actions.Select(action => action.ActionId).Order().ToArray());

    private static CanonicalObservationTrace OrderSensitiveEvaluator(IReadOnlyList<PersistenceAction> actions)
        => Trace(actions.Select(action => action.ActionId).ToArray());

    private static CanonicalObservationTrace Trace(long[] actionIds)
    {
        if (actionIds.Length == 0)
        {
            return new CanonicalObservationTrace([]);
        }

        var digest = string.Join(',', actionIds);
        return new CanonicalObservationTrace(
        [
            new ObservationTracePoint(
                ResearchEventKind.RecoveryCompleted,
                HistoryA,
                ResearchDurabilityPhase.AuthorityPublished,
                ObservationAvailability.Ready,
                ObservationErrorKind.None,
                false,
                1,
                SafetyPredicateMask.NoPhantomCommit | SafetyPredicateMask.NoCrossHistoryReplay,
                digest,
                null),
        ]);
    }
}
