using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.History.Snapshots;

namespace ChronicleDB.UnitTests.History;

public sealed class SnapshotCatalogTests
{
    [Fact]
    public void PersistedDefinitionsAreIndexedByIdAndName()
    {
        var catalog = new SnapshotCatalog(new CommitSequence(3), new CommitSequence(10));
        var definition = new SnapshotDefinition(
            SnapshotId.New(),
            "release-candidate",
            new CommitSequence(7),
            1_700_000_000_000);

        catalog.RegisterPersisted(definition, new CommitSequence(10));

        Assert.True(catalog.TryGet(definition.SnapshotId, out var byId));
        Assert.Equal(definition, byId);
        Assert.True(catalog.TryGet("release-candidate", out var byName));
        Assert.Equal(definition, byName);
    }

    [Fact]
    public void DuplicateActiveNameIsRejectedBeforePersistence()
    {
        var catalog = new SnapshotCatalog(CommitSequence.Initial, new CommitSequence(5));
        var first = catalog.PrepareCreate("stable", new CommitSequence(5));
        catalog.RegisterPersisted(first, new CommitSequence(5));

        Assert.Throws<InvalidOperationException>(
            () => catalog.PrepareCreate("stable", new CommitSequence(5)));
    }

    [Fact]
    public void RecoveredSnapshotBelowGenericFloorRemainsAvailableAsAnExplicitRoot()
    {
        var definition = new SnapshotDefinition(
            SnapshotId.New(),
            "too-old",
            new CommitSequence(4),
            1);

        var catalog = new SnapshotCatalog(
            new CommitSequence(5),
            new CommitSequence(10),
            [definition]);

        Assert.True(catalog.TryGet(definition.SnapshotId, out var recovered));
        Assert.Equal(definition, recovered);
    }

    [Fact]
    public void RecoveredSnapshotBeyondCurrentHistoryIsRejected()
    {
        var definition = new SnapshotDefinition(
            SnapshotId.New(),
            "future",
            new CommitSequence(11),
            1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SnapshotCatalog(
                new CommitSequence(5),
                new CommitSequence(10),
                [definition]));
    }


    [Fact]
    public void SnapshotNamesUseBoundedUtf8AndRejectWhitespaceAmbiguity()
    {
        var catalog = new SnapshotCatalog(CommitSequence.Initial, CommitSequence.Initial);
        var maximumAscii = new string('a', SnapshotCatalog.MaxNameBytes);

        var definition = catalog.PrepareCreate(maximumAscii, CommitSequence.Initial);
        Assert.Equal(maximumAscii, definition.Name);
        Assert.Throws<ArgumentException>(() => catalog.PrepareCreate(" padded", CommitSequence.Initial));
        Assert.Throws<ArgumentException>(
            () => catalog.PrepareCreate(new string('é', SnapshotCatalog.MaxNameBytes), CommitSequence.Initial));
        Assert.Throws<ArgumentException>(
            () => catalog.PrepareCreate("invalid-\ud800", CommitSequence.Initial));
    }

    [Fact]
    public void RemovingSnapshotReleasesItsNameForLaterReuse()
    {
        var catalog = new SnapshotCatalog(CommitSequence.Initial, new CommitSequence(2));
        var first = catalog.PrepareCreate("nightly", new CommitSequence(2));
        catalog.RegisterPersisted(first, new CommitSequence(2));
        catalog.RemoveRequired(first.SnapshotId);

        var second = catalog.PrepareCreate("nightly", new CommitSequence(2));

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
    }
}
