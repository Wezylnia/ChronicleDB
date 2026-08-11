using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class PersistenceProtocolOracleTests
{
    [Fact]
    public void SafeDurabilityThenPublicationHasNoViolations()
    {
        var history = new HistoryId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var operation = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var evaluation = PersistenceProtocolOracle.Evaluate(
        [
            new PersistenceAction(
                1,
                ResearchEventKind.DurabilityBarrier,
                history,
                null,
                operation,
                ["main.wal"],
                ResearchDurabilityPhase.StableStorageBarrier,
                3),
            new PersistenceAction(
                2,
                ResearchEventKind.AuthorityPublished,
                history,
                null,
                operation,
                ["main.wal"],
                ResearchDurabilityPhase.AuthorityPublished,
                3,
                [1]),
        ]);

        Assert.True(evaluation.IsSafe);
        Assert.Empty(evaluation.Violations);
    }

    [Fact]
    public void MutationCorpusKillsEveryDeclaredPilotMutant()
    {
        var results = PersistenceMutationCorpus.Evaluate();

        Assert.Equal(9, results.Count);
        Assert.All(results, result => Assert.True(result.Killed, result.Name));
    }

    [Fact]
    public void PerHistoryProjectionNormalizesIndependentInterleavingOrder()
    {
        var historyA = new HistoryId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var historyB = new HistoryId(Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var actionA = new PersistenceAction(
            1,
            ResearchEventKind.DurabilityBarrier,
            historyA,
            null,
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            ["a.wal"],
            ResearchDurabilityPhase.StableStorageBarrier,
            1);
        var actionB = new PersistenceAction(
            2,
            ResearchEventKind.DurabilityBarrier,
            historyB,
            null,
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
            ["b.wal"],
            ResearchDurabilityPhase.StableStorageBarrier,
            1);

        var left = PersistenceProtocolOracle.Evaluate([actionA, actionB]);
        var right = PersistenceProtocolOracle.Evaluate([actionB, actionA]);

        Assert.True(left.Trace.EquivalentTo(right.Trace));
    }
}
