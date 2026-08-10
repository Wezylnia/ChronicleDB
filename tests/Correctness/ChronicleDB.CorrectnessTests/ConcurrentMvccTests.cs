using ChronicleDB.Transactions.Faults;

namespace ChronicleDB.CorrectnessTests;

public sealed class ConcurrentMvccTests
{
    [Fact]
    public async Task IndependentWritersFromSameSnapshotCommitWithoutLostUpdates()
    {
        var directory = NewDirectory();
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            const int writerCount = 16;
            var transactions = Enumerable.Range(0, writerCount)
                .Select(index =>
                {
                    var transaction = database.BeginTransaction();
                    transaction.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index * 10));
                    return transaction;
                })
                .ToArray();

            Assert.All(transactions, transaction => Assert.Equal((ulong)0, transaction.StartSequence));
            await Task.WhenAll(transactions.Select(transaction => Task.Run(transaction.Commit)));

            Assert.Equal((ulong)writerCount, database.CurrentCommitSequence.Value);
            Assert.Equal(writerCount, database.Count);
            Assert.Equal(
                Enumerable.Range(1, writerCount).Select(value => (ulong)value),
                transactions.Select(transaction => transaction.CommitSequence!.Value).Order());
            for (var index = 0; index < writerCount; index++)
            {
                Assert.True(database.TryGet(BitConverter.GetBytes(index), out var value));
                Assert.Equal(BitConverter.GetBytes(index * 10), value);
            }

            foreach (var transaction in transactions)
            {
                transaction.Dispose();
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SameKeyWritersFromSameSnapshotHaveExactlyOneWinner()
    {
        var directory = NewDirectory();
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            const int writerCount = 12;
            var transactions = Enumerable.Range(0, writerCount)
                .Select(index =>
                {
                    var transaction = database.BeginTransaction();
                    transaction.Put([1], [checked((byte)(index + 1))]);
                    return transaction;
                })
                .ToArray();
            var results = await Task.WhenAll(transactions.Select(
                transaction => Task.Run(() =>
                {
                    try
                    {
                        transaction.Commit();
                        return true;
                    }
                    catch (ChronicleDB.TransactionConflictException)
                    {
                        return false;
                    }
                })));

            Assert.Equal(1, results.Count(result => result));
            Assert.Equal(writerCount - 1, results.Count(result => !result));
            Assert.Equal((ulong)1, database.CurrentCommitSequence.Value);
            Assert.True(database.TryGet([1], out _));

            foreach (var transaction in transactions)
            {
                transaction.Dispose();
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadersRetainStableSnapshotsWhileWriterCommits()
    {
        var directory = NewDirectory();
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            database.Put([1], [10]);
            var readers = Enumerable.Range(0, 8)
                .Select(_ => database.BeginTransaction())
                .ToArray();

            var writer = Task.Run(() =>
            {
                for (byte value = 11; value < 31; value++)
                {
                    database.Put([1], [value]);
                }
            });
            var readerTasks = readers.Select(reader => Task.Run(() =>
            {
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    Assert.True(reader.TryGet([1], out var value));
                    Assert.Equal(new byte[] { 10 }, value);
                }
            }));

            await Task.WhenAll(readerTasks.Append(writer));
            Assert.True(database.TryGet([1], out var latest));
            Assert.Equal(new byte[] { 30 }, latest);

            foreach (var reader in readers)
            {
                reader.Abort();
                reader.Dispose();
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task MultiKeyCommittedStateIsAtomicToTransactionSnapshots()
    {
        var directory = NewDirectory();
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            using (var seed = database.BeginTransaction())
            {
                seed.Put([1], [0]);
                seed.Put([2], [0]);
                seed.Commit();
            }

            var writer = Task.Run(() =>
            {
                for (byte version = 1; version <= 20; version++)
                {
                    using var transaction = database.BeginTransaction();
                    transaction.Put([1], [version]);
                    transaction.Put([2], [version]);
                    transaction.Commit();
                }
            });
            var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            {
                for (var iteration = 0; iteration < 80; iteration++)
                {
                    using var transaction = database.BeginTransaction();
                    Assert.True(transaction.TryGet([1], out var first));
                    Assert.True(transaction.TryGet([2], out var second));
                    Assert.Equal(first, second);
                    transaction.Abort();
                }
            }));

            await Task.WhenAll(readers.Append(writer));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadersDoNotBlockBehindDurableWriterBeforeLogicalPublication()
    {
        var directory = NewDirectory();
        try
        {
            using var injector = new PausingFaultInjector(TransactionFaultPoint.AfterWalFlush);
            using var database = ChronicleDB.ChronicleDatabase.Open(directory, faultInjector: injector);
            database.Put([1], [10]);
            using var oldReader = database.BeginTransaction();
            injector.Arm();

            var writer = Task.Run(() => database.Put([1], [20]));
            var reached = injector.WaitUntilReached(TimeSpan.FromSeconds(10));
            if (!reached)
            {
                // Never leave a potentially blocked worker behind when this assertion fails.
                injector.Release();
                await writer;
            }

            Assert.True(reached);
            try
            {
                // The WAL decision is already durable, but current logical publication has
                // not occurred. Neither a current read nor transaction construction should
                // wait behind the commit coordinator, and both must observe sequence 1.
                Assert.True(database.TryGet([1], out var currentBeforePublication));
                Assert.Equal(new byte[] { 10 }, currentBeforePublication);
                Assert.True(oldReader.TryGet([1], out var oldSnapshot));
                Assert.Equal(new byte[] { 10 }, oldSnapshot);
                using var concurrentReader = database.BeginTransaction();
                Assert.Equal((ulong)1, concurrentReader.StartSequence);
                Assert.True(concurrentReader.TryGet([1], out var concurrentValue));
                Assert.Equal(new byte[] { 10 }, concurrentValue);
                concurrentReader.Abort();
            }
            finally
            {
                injector.Release();
            }

            await writer;
            Assert.True(database.TryGet([1], out var currentAfterPublication));
            Assert.Equal(new byte[] { 20 }, currentAfterPublication);
            oldReader.Abort();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentCommittedWorkloadRecoversAllKeys()
    {
        var directory = NewDirectory();
        try
        {
            const int writerCount = 12;
            using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                await Task.WhenAll(Enumerable.Range(0, writerCount).Select(index => Task.Run(() =>
                {
                    using var transaction = database.BeginTransaction();
                    transaction.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index + 100));
                    transaction.Commit();
                })));
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            Assert.Equal(writerCount, reopened.Count);
            for (var index = 0; index < writerCount; index++)
            {
                Assert.True(reopened.TryGet(BitConverter.GetBytes(index), out var value));
                Assert.Equal(BitConverter.GetBytes(index + 100), value);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private sealed class PausingFaultInjector(TransactionFaultPoint target) : ITransactionFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim _reached = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public bool WaitUntilReached(TimeSpan timeout) => _reached.Wait(timeout);

        public void Release() => _release.Set();

        public void Hit(TransactionFaultPoint point)
        {
            if (Volatile.Read(ref _armed) == 0 || point != target)
            {
                return;
            }

            _reached.Set();
            _release.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _reached.Dispose();
            _release.Dispose();
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class TransactionHandleConcurrencyTests
{
    [Fact]
    public async Task AbortCannotRaceCommitPreparationOnSamePublicHandle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-handle-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var injector = new BlockingFaultInjector();
            using var database = ChronicleDB.ChronicleDatabase.Open(directory, faultInjector: injector);
            var transaction = database.BeginTransaction();
            transaction.Put([1], [10]);

            var commit = Task.Run(transaction.Commit);
            Assert.True(injector.Entered.Wait(TimeSpan.FromSeconds(10)));
            var abort = Task.Run(() => Assert.Throws<ObjectDisposedException>(transaction.Abort));
            await Task.Delay(100);
            Assert.False(abort.IsCompleted);

            injector.Release.Set();
            await commit;
            await abort;
            Assert.True(database.TryGet([1], out var value));
            Assert.Equal(new byte[] { 10 }, value);
            transaction.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class BlockingFaultInjector : ITransactionFaultInjector, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Hit(TransactionFaultPoint point)
        {
            if (point != TransactionFaultPoint.BeforeWalAppend)
            {
                return;
            }

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Test did not release the commit fault point.");
            }
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
