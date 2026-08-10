using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.History.Roots;

namespace ChronicleDB.UnitTests.History;

public sealed class HistoryRootRegistryTests
{
    [Fact]
    public void ActiveRootsAndBaselineContributeToHistorySpecificRetention()
    {
        var main = HistoryId.New();
        var other = HistoryId.New();
        var registry = new HistoryRootRegistry(main, new CommitSequence(8));
        var root = CreateRoot(main, new CommitSequence(12));
        var otherRoot = CreateRoot(other, new CommitSequence(3), HistoryRootKind.BranchBase, HistoryId.New());

        registry.RegisterActive(root);
        registry.RegisterActive(otherRoot);

        Assert.Equal(new CommitSequence(8), registry.GetRetentionFloor(main));
        Assert.Equal(new CommitSequence(3), registry.GetRetentionFloor(other));
        Assert.Single(registry.GetRetentionRequirements(main));
    }

    [Fact]
    public void DeleteLifecycleKeepsRootProtectedUntilCompletion()
    {
        var history = HistoryId.New();
        var registry = new HistoryRootRegistry(history, new CommitSequence(10));
        var root = CreateRoot(history, new CommitSequence(4));
        registry.RegisterActive(root);

        registry.BeginDelete(root.RootId);
        Assert.Equal(new CommitSequence(4), registry.GetRetentionFloor(history));

        registry.CompleteDelete(root.RootId);
        Assert.Equal(new CommitSequence(10), registry.GetRetentionFloor(history));
        Assert.True(registry.TryGet(root.RootId, out var deleted));
        Assert.Equal(HistoryRootState.Deleted, deleted!.State);
    }

    [Fact]
    public void InvalidLifecycleTransitionsAndDuplicateRootsAreRejected()
    {
        var history = HistoryId.New();
        var registry = new HistoryRootRegistry(history, CommitSequence.Initial);
        var root = CreateRoot(history, CommitSequence.Initial);

        registry.RegisterCreating(root with { State = HistoryRootState.Creating });
        Assert.Throws<InvalidOperationException>(() => registry.BeginDelete(root.RootId));
        registry.Activate(root.RootId);
        Assert.Throws<InvalidOperationException>(() => registry.Activate(root.RootId));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterActive(root));
    }

    [Fact]
    public void RecoveredCreatingRootIsRetainedConservatively()
    {
        var history = HistoryId.New();
        var root = CreateRoot(history, new CommitSequence(2), state: HistoryRootState.Creating);
        var registry = new HistoryRootRegistry(history, new CommitSequence(5), [root]);

        Assert.Equal(new CommitSequence(2), registry.GetRetentionFloor(history));
        Assert.Equal(HistoryRootState.Creating, registry.ListActive().Single().State);
    }

    private static HistoryRoot CreateRoot(
        HistoryId history,
        CommitSequence boundary,
        HistoryRootKind kind = HistoryRootKind.PersistentSnapshot,
        HistoryId? parentHistory = null,
        HistoryRootState state = HistoryRootState.Active)
        => new(
            HistoryRootId.New(),
            kind,
            Guid.NewGuid(),
            history,
            parentHistory ?? HistoryId.Empty,
            boundary,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            state);
}
