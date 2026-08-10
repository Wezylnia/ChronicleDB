using ChronicleDB.Core.Keys;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;

namespace ChronicleDB.PersistenceTests;

public sealed class StorageBoundaryTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void InlineThresholdBoundaryUsesExpectedPageRepresentation(int delta, long expectedPages)
    {
        using var directory = new StorageTestDirectory();
        var options = new StorageOptions { FlushOnWrite = false };
        var key = new BinaryKey([1]);
        var length = checked(options.InlineValueCapacity(key.Length) + delta);
        var value = Enumerable.Range(0, length).Select(index => checked((byte)(index % 251))).ToArray();

        using var store = PersistentKeyValueStore.Open(directory.Path, options);
        store.Put(key, value);

        Assert.Equal(expectedPages, store.PageCount);
        Assert.True(store.TryGet(key, out var stored));
        Assert.Equal(value, stored);
    }

    [Fact]
    public void MaximumConfiguredKeyLengthIsAcceptedAndNextByteIsRejected()
    {
        using var directory = new StorageTestDirectory();
        var options = new StorageOptions { FlushOnWrite = false };
        using var store = PersistentKeyValueStore.Open(directory.Path, options);

        var maximum = new BinaryKey(new byte[options.MaxKeySize]);
        store.Put(maximum, [7]);
        Assert.True(store.TryGet(maximum, out var value));
        Assert.Equal(new byte[] { 7 }, value);

        Assert.Throws<StorageLimitException>(
            () => store.Put(new BinaryKey(new byte[options.MaxKeySize + 1]), [8]));
    }

    [Fact]
    public void ConfiguredValueLimitAcceptsExactBoundaryAndRejectsNextByte()
    {
        using var directory = new StorageTestDirectory();
        var options = new StorageOptions
        {
            MaxValueSize = 32 * 1024,
            FlushOnWrite = false
        };
        using var store = PersistentKeyValueStore.Open(directory.Path, options);

        var exact = new byte[options.MaxValueSize];
        exact[^1] = 1;
        store.Put(new BinaryKey([1]), exact);
        Assert.True(store.TryGet(new BinaryKey([1]), out var value));
        Assert.Equal(exact, value);

        Assert.Throws<StorageLimitException>(
            () => store.Put(new BinaryKey([2]), new byte[options.MaxValueSize + 1]));
    }
}
