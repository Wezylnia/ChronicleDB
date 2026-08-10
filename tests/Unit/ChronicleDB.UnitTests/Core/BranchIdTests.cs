using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.UnitTests.Core;

public sealed class BranchIdTests
{
    [Fact]
    public void NewBranchIdIsValidAndEmptyIsNot()
    {
        Assert.False(BranchId.Empty.IsValid);
        var id = BranchId.New();
        Assert.True(id.IsValid);
        Assert.NotEqual(Guid.Empty, id.Value);
    }
}
