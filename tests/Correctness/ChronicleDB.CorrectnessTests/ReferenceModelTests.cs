using ChronicleDB.ReferenceModel;

namespace ChronicleDB.CorrectnessTests;

public sealed class ReferenceModelTests
{
    [Fact]
    public void ModelUsesBinaryKeyIdentityAndOwnsValues()
    {
        var model = new ReferenceKeyValueModel();
        var key = new byte[] { 0, 255 };
        var value = new byte[] { 1, 2 };
        model.Put(key, value);
        key[0] = 9;
        value[0] = 9;

        Assert.True(model.TryGet([0, 255], out var actual));
        Assert.Equal(new byte[] { 1, 2 }, actual);
        Assert.False(model.TryGet([0, 254], out _));
        Assert.True(model.Delete([0, 255]));
        Assert.Equal(0, model.Count);
    }
}
