using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.History.Branches;

namespace ChronicleDB.UnitTests.History;

public sealed class BranchCatalogTests
{
    [Fact]
    public void CatalogEnforcesIdentityNameAndHistoryUniqueness()
    {
        var branch = CreateBranch("A");
        var catalog = new BranchCatalog([branch]);

        Assert.True(catalog.TryGet(branch.BranchId, out var byId));
        Assert.Equal(branch, byId);
        Assert.True(catalog.TryGet("A", out var byName));
        Assert.Equal(branch, byName);
        Assert.True(catalog.TryGetByHistory(branch.HistoryId, out var byHistory));
        Assert.Equal(branch, byHistory);
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterActive(branch));
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterActive(
            branch with { BranchId = BranchId.New(), HistoryId = HistoryId.New() }));
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterActive(
            branch with { BranchId = BranchId.New(), Name = "B" }));
    }

    [Fact]
    public void PublishCommitMustAdvanceExactlyOneLocalSequence()
    {
        var branch = CreateBranch("A");
        var catalog = new BranchCatalog([branch]);

        var updated = catalog.PublishCommit(branch.BranchId, CommitSequence.Initial, new CommitSequence(1));
        Assert.Equal(new CommitSequence(1), updated.LocalCurrentSequence);
        Assert.Throws<InvalidOperationException>(
            () => catalog.PublishCommit(branch.BranchId, CommitSequence.Initial, new CommitSequence(1)));
        Assert.Throws<InvalidOperationException>(
            () => catalog.PublishCommit(branch.BranchId, new CommitSequence(1), new CommitSequence(3)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void InvalidNamesAreRejected(string name)
        => Assert.ThrowsAny<ArgumentException>(() => BranchCatalog.ValidateName(name));

    private static BranchDefinition CreateBranch(string name)
        => new(
            BranchId.New(),
            name,
            Guid.NewGuid(),
            HistoryId.New(),
            HistoryId.New(),
            HistoryRootId.New(),
            new CommitSequence(7),
            CommitSequence.Initial,
            Guid.NewGuid(),
            100,
            1,
            BranchLifecycleState.Active);
}
