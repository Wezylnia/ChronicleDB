using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.UnitTests.Core;

public sealed class HistoryRootIdTests
{
    [Fact]
    public void NewRootIdIsValidAndStable()
    {
        var id = HistoryRootId.New();

        Assert.True(id.IsValid);
        Assert.Equal(id, new HistoryRootId(id.Value));
        Assert.NotEqual(HistoryRootId.Empty, id);
    }
}
