using ChronicleDB.Core.Sequences;
using ChronicleDB.Mvcc.Versions;
using ChronicleDB.Mvcc.Visibility;

namespace ChronicleDB.UnitTests.Mvcc;

public sealed class VersionVisibilityTests
{
    [Fact]
    public void CommittedVersionAtBoundaryIsVisible()
    {
        var version = VersionMetadata.Committed(new CommitSequence(7));

        Assert.True(VersionVisibility.IsVisible(version, new CommitSequence(7)));
    }

    [Fact]
    public void VersionCommittedAfterBoundaryIsNotVisible()
    {
        var version = VersionMetadata.Committed(new CommitSequence(8));

        Assert.False(VersionVisibility.IsVisible(version, new CommitSequence(7)));
    }

    [Theory]
    [InlineData(VersionState.Pending)]
    [InlineData(VersionState.Aborted)]
    public void NonCommittedVersionIsNotVisible(VersionState state)
    {
        var version = state == VersionState.Pending
            ? VersionMetadata.Pending()
            : VersionMetadata.Aborted();

        Assert.False(VersionVisibility.IsVisible(version, new CommitSequence(100)));
    }

    [Fact]
    public void CommittedVersionRequiresNonZeroSequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VersionMetadata.Committed(CommitSequence.Initial));
    }
}
