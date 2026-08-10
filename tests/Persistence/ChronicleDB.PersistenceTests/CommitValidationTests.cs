using ChronicleDB.Core.Identifiers;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.PersistenceTests;

public sealed class CommitValidationTests
{
    [Fact]
    public void StorageLimitIsRejectedBeforeAnyWalRecordIsWritten()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDB.ChronicleDatabase.Open(
                   directory.Path,
                   new StorageOptions { MaxKeySize = 8 }))
        {
            using var transaction = database.BeginTransaction();
            transaction.Put(new byte[9], [1]);

            Assert.Throws<StorageLimitException>(() => transaction.Commit());
            Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
            Assert.False(database.TryGet(new byte[9], out _));
        }

        using var wal = WalLog.Open(directory.Path);
        Assert.Empty(wal.ReadAll());
    }

    [Fact]
    public void DurableCommitTransitionsTransactionBeforePhysicalPublication()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowAfterWalFlushInjector();
        using var database = ChronicleDB.ChronicleDatabase.Open(directory.Path, faultInjector: injector);
        using var transaction = database.BeginTransaction();
        transaction.Put([1], [2]);

        Assert.Throws<InvalidOperationException>(() => transaction.Commit());
        Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
        transaction.Dispose();
        Assert.Throws<ChronicleDB.ChronicleDatabaseFaultedException>(() => database.TryGet([1], out _));
    }

    [Fact]
    public void FailureBeforeFirstWalAppendAbortsTransactionWithoutFaultingDatabase()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDB.ChronicleDatabase.Open(
            directory.Path,
            faultInjector: new ThrowBeforeWalAppendInjector());
        using var transaction = database.BeginTransaction();
        transaction.Put([1], [2]);

        Assert.Throws<InvalidOperationException>(() => transaction.Commit());
        Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
        Assert.False(database.TryGet([1], out _));
        Assert.Throws<ObjectDisposedException>(() => transaction.Put([2], [3]));
    }

    [Fact]
    public void DatabaseRejectsValueLimitThatCannotBeRepresentedByWal()
    {
        using var directory = new StorageTestDirectory();
        Assert.Throws<StorageLimitException>(() => ChronicleDB.ChronicleDatabase.Open(
            directory.Path,
            new StorageOptions { MaxValueSize = 64 * 1024 * 1024 + 1 }));
    }

    [Fact]
    public void PhysicalPageFaultLeavesDatabaseFaultedAndRecoveryReplaysWal()
    {
        using var directory = new StorageTestDirectory();
        var injector = new ThrowAfterFirstPageInjector();
        var options = new StorageOptions { FaultInjector = injector };
        var database = ChronicleDB.ChronicleDatabase.Open(directory.Path, options);
        var value = new byte[options.InlineValueCapacity(1) + 32];
        Assert.Throws<InvalidOperationException>(() => database.Put([1], value));
        Assert.Equal(ChronicleDB.DatabaseState.Faulted, database.State);
        database.Dispose();

        using var recovered = ChronicleDB.ChronicleDatabase.Open(directory.Path);
        Assert.True(recovered.TryGet([1], out var actual));
        Assert.Equal(value, actual);
    }

    private sealed class ThrowAfterWalFlushInjector : ChronicleDB.Transactions.Faults.ITransactionFaultInjector
    {
        public void Hit(ChronicleDB.Transactions.Faults.TransactionFaultPoint point)
        {
            if (point == ChronicleDB.Transactions.Faults.TransactionFaultPoint.AfterWalFlush)
            {
                throw new InvalidOperationException("Injected post-durability failure.");
            }
        }
    }

    private sealed class ThrowBeforeWalAppendInjector : ChronicleDB.Transactions.Faults.ITransactionFaultInjector
    {
        public void Hit(ChronicleDB.Transactions.Faults.TransactionFaultPoint point)
        {
            if (point == ChronicleDB.Transactions.Faults.TransactionFaultPoint.BeforeWalAppend)
            {
                throw new InvalidOperationException("Injected pre-WAL failure.");
            }
        }
    }

    private sealed class ThrowAfterFirstPageInjector : IStorageFaultInjector
    {
        private int _writes;

        public void Hit(StorageFaultPoint point, PageId pageId)
        {
            if (point == StorageFaultPoint.AfterPageWrite && Interlocked.Increment(ref _writes) == 1)
            {
                throw new InvalidOperationException($"Injected physical page fault at {pageId}.");
            }
        }
    }
}
