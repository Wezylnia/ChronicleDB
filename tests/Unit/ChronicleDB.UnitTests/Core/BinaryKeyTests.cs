using ChronicleDB.Core.Keys;

namespace ChronicleDB.UnitTests.Core;

public sealed class BinaryKeyTests
{
    [Fact]
    public void ConstructorCopiesCallerOwnedBytes()
    {
        byte[] source = [1, 2, 3];
        var key = new BinaryKey(source);

        source[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, key.ToArray());
    }

    [Fact]
    public void EqualContentHasValueEquality()
    {
        var first = new BinaryKey([1, 2, 3]);
        var second = new BinaryKey([1, 2, 3]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void LexicographicComparerOrdersByRawBytes()
    {
        var keys = new[]
        {
            new BinaryKey([0x01, 0x10]),
            new BinaryKey([0x01]),
            new BinaryKey([0x00, 0xFF]),
            new BinaryKey([0x01, 0x02]),
        };

        Array.Sort(keys, BinaryKeyLexicographicComparer.Instance);

        Assert.Equal(new byte[] { 0x00, 0xFF }, keys[0].ToArray());
        Assert.Equal(new byte[] { 0x01 }, keys[1].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x02 }, keys[2].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x10 }, keys[3].ToArray());
    }

    [Fact]
    public void FullKeyParticipatesInEquality()
    {
        var first = new BinaryKey([1, 2, 3]);
        var second = new BinaryKey([1, 2, 4]);

        Assert.NotEqual(first, second);
    }
}
