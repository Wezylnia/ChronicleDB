using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.UnitTests.Core;

public sealed class SnapshotIdTests
{
    [Fact]
    public void NewProducesValidStableIdentity()
    {
        var id = SnapshotId.New();
        Assert.True(id.IsValid);
        Assert.NotEqual(SnapshotId.Empty, id);
        Assert.Equal(id, new SnapshotId(id.Value));
    }
}
