using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.Files;

namespace ChronicleDB.PersistenceTests;

public sealed class StorageFaultStateTests
{
    [Fact]
    public void FailureBeforeFirstPageWriteDoesNotFaultStore()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowOnceInjector(StorageFaultPoint.BeforePageWrite);
        using var store = PersistentKeyValueStore.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });

        Assert.Throws<InvalidOperationException>(() => store.Put(new BinaryKey([1]), [10]));
        Assert.False(store.IsFaulted);

        store.Put(new BinaryKey([2]), [20]);
        Assert.True(store.TryGet(new BinaryKey([2]), out var value));
        Assert.Equal(new byte[] { 20 }, value);
    }

    [Fact]
    public void FailureAfterPageWriteFaultsStoreUntilReopen()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowOnceInjector(StorageFaultPoint.AfterPageWrite);
        using var store = PersistentKeyValueStore.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });

        Assert.Throws<InvalidOperationException>(() => store.Put(new BinaryKey([1]), [10]));
        Assert.True(store.IsFaulted);
        Assert.Throws<StorageException>(() => store.TryGet(new BinaryKey([1]), out _));
        Assert.Throws<StorageException>(() => store.Put(new BinaryKey([2]), [20]));
        store.Dispose();

        using var reopened = PersistentKeyValueStore.Open(directory.Path);
        // The write completed at page granularity before the injected failure. Reopen is
        // the authority on the physical outcome; importantly, the faulted instance was
        // never allowed to continue from an uncertain in-memory state.
        Assert.True(reopened.TryGet(new BinaryKey([1]), out var value));
        Assert.Equal(new byte[] { 10 }, value);
    }

    [Fact]
    public void FailureBeforeLaterOverflowPageStillFaultsStoreBecauseOperationAlreadyTouchedDisk()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowOnNthBeforeWriteInjector(2);
        using var store = PersistentKeyValueStore.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });

        var largeValue = Enumerable.Repeat((byte)7, 40_000).ToArray();
        Assert.Throws<InvalidOperationException>(
            () => store.Put(new BinaryKey([1]), largeValue));
        Assert.True(store.IsFaulted);
        Assert.Throws<StorageException>(() => store.TryGet(new BinaryKey([1]), out _));
    }


    [Fact]
    public void CompactionFailureBeforePublicationDoesNotFaultStore()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowOnceInjector(StorageFaultPoint.BeforeCompactionPublish);
        using var store = PersistentKeyValueStore.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });
        store.Put(new BinaryKey([1]), [10]);

        var desired = store.SnapshotCurrentState();
        Assert.Throws<InvalidOperationException>(() => store.RewriteState(desired));
        Assert.False(store.IsFaulted);
        Assert.True(store.TryGet(new BinaryKey([1]), out var value));
        Assert.Equal(new byte[] { 10 }, value);
    }

    [Fact]
    public void CompactionFailureAfterPublicationFaultsStoreUntilReopen()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowOnceInjector(StorageFaultPoint.AfterCompactionPublish);
        using var store = PersistentKeyValueStore.Open(
            directory.Path,
            new StorageOptions { FaultInjector = injector });
        store.Put(new BinaryKey([1]), [10]);
        store.Put(new BinaryKey([2]), [20]);

        var desired = store.SnapshotCurrentState();
        Assert.Throws<InvalidOperationException>(() => store.RewriteState(desired));
        Assert.True(store.IsFaulted);
        Assert.Throws<StorageException>(() => store.TryGet(new BinaryKey([1]), out _));
        store.Dispose();

        using var reopened = PersistentKeyValueStore.Open(directory.Path);
        Assert.True(reopened.TryGet(new BinaryKey([1]), out var first));
        Assert.Equal(new byte[] { 10 }, first);
        Assert.True(reopened.TryGet(new BinaryKey([2]), out var second));
        Assert.Equal(new byte[] { 20 }, second);
    }

    [Fact]
    public void InterruptedCompactionKeepsPreviousGenerationUntilPublishedPrimaryValidates()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowOnceInjector(StorageFaultPoint.AfterCompactionPublish);
        using (var store = PersistentKeyValueStore.Open(
                   directory.Path,
                   new StorageOptions { FaultInjector = injector }))
        {
            store.Put(new BinaryKey([1]), [10]);
            store.Put(new BinaryKey([2]), [20]);
            var desired = store.SnapshotCurrentState();
            Assert.Throws<InvalidOperationException>(() => store.RewriteState(desired));
        }

        var dataPath = Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        var backupPath = dataPath + ".previous";
        Assert.True(File.Exists(backupPath));
        var bytes = File.ReadAllBytes(dataPath);
        bytes[Math.Min(100, bytes.Length - 1)] ^= 0x5A;
        File.WriteAllBytes(dataPath, bytes);

        using var reopened = PersistentKeyValueStore.Open(directory.Path);
        Assert.False(File.Exists(backupPath));
        Assert.True(reopened.TryGet(new BinaryKey([1]), out var first));
        Assert.Equal(new byte[] { 10 }, first);
        Assert.True(reopened.TryGet(new BinaryKey([2]), out var second));
        Assert.Equal(new byte[] { 20 }, second);
    }

    private sealed class ThrowOnceInjector(StorageFaultPoint target) : IStorageFaultInjector
    {
        private int _thrown;

        public void Hit(StorageFaultPoint point, PageId pageId)
        {
            if (point == target && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("Injected storage fault.");
            }
        }
    }

    private sealed class ThrowOnNthBeforeWriteInjector(int targetWrite) : IStorageFaultInjector
    {
        private int _writes;

        public void Hit(StorageFaultPoint point, PageId pageId)
        {
            if (point == StorageFaultPoint.BeforePageWrite
                && Interlocked.Increment(ref _writes) == targetWrite)
            {
                throw new InvalidOperationException($"Injected storage fault before page {pageId.Value}.");
            }
        }
    }

}
