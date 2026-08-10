using ChronicleDB.Core.Keys;
using ChronicleDB.Indexing;
using ChronicleDB.Indexing.Baseline;

namespace ChronicleDB.UnitTests.Indexing;

public sealed class SynchronizedVersionIndexTests
{
    [Fact]
    public void EqualBinaryKeyFindsPublishedHead()
    {
        var index = new SynchronizedVersionIndex();
        index.Publish(new BinaryKey([1, 2, 3]), new VersionHandle(17));

        var found = index.TryGet(new BinaryKey([1, 2, 3]), out var head);

        Assert.True(found);
        Assert.Equal(new VersionHandle(17), head);
    }

    [Fact]
    public void PublishReplacesHeadWithoutAddingDuplicateKey()
    {
        var index = new SynchronizedVersionIndex();
        var key = new BinaryKey([4, 5, 6]);

        index.Publish(key, new VersionHandle(1));
        index.Publish(key, new VersionHandle(2));

        Assert.Equal(1, index.Count);
        Assert.True(index.TryGet(key, out var head));
        Assert.Equal(new VersionHandle(2), head);
    }

    [Fact]
    public void InvalidHeadIsRejected()
    {
        var index = new SynchronizedVersionIndex();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => index.Publish(new BinaryKey([1]), VersionHandle.Invalid));
    }

    [Fact]
    public void ConcurrentWritersPublishAllDistinctKeys()
    {
        const int entryCount = 512;
        var index = new SynchronizedVersionIndex();

        Parallel.For(
            0,
            entryCount,
            value => index.Publish(
                new BinaryKey(BitConverter.GetBytes(value)),
                new VersionHandle((ulong)value + 1)));

        Assert.Equal(entryCount, index.Count);
    }
}
