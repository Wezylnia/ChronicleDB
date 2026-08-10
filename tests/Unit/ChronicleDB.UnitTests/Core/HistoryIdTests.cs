using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.UnitTests.Core;

public sealed class HistoryIdTests
{
    [Fact]
    public void NewHistoryIdIsValidAndRoundTripsValue()
    {
        var id = HistoryId.New();

        Assert.True(id.IsValid);
        Assert.Equal(id, new HistoryId(id.Value));
        Assert.NotEqual(HistoryId.Empty, id);
    }

    [Fact]
    public void EmptyHistoryIdIsInvalid()
    {
        Assert.False(HistoryId.Empty.IsValid);
    }
}
