namespace ChronicleDB.CorrectnessTests;

public sealed class ConcurrentStressProfileTests
{
    [Theory]
    [InlineData(95)]
    [InlineData(50)]
    [InlineData(20)]
    public async Task ReadBalancedAndWriteHeavyProfilesPreservePerKeyOrder(int readPercent)
    {
        var directory = NewDirectory();
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            const int workers = 4;
            const int operationsPerWorker = 30;
            var expected = new int?[workers];

            await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
            {
                var random = new Random(1_000 + readPercent * 31 + worker);
                var key = BitConverter.GetBytes(50_000 + worker);
                for (var operation = 0; operation < operationsPerWorker; operation++)
                {
                    if (random.Next(100) < readPercent)
                    {
                        _ = database.TryGet(key, out _);
                        continue;
                    }

                    var value = worker * 10_000 + operation;
                    using var transaction = database.BeginTransaction();
                    transaction.Put(key, BitConverter.GetBytes(value));
                    transaction.Commit();
                    expected[worker] = value;
                }
            })));

            for (var worker = 0; worker < workers; worker++)
            {
                var key = BitConverter.GetBytes(50_000 + worker);
                if (expected[worker] is { } expectedValue)
                {
                    Assert.True(database.TryGet(key, out var actual));
                    Assert.Equal(BitConverter.GetBytes(expectedValue), actual);
                }
                else
                {
                    Assert.False(database.TryGet(key, out _));
                }
            }

            Assert.Equal(ChronicleDB.DatabaseState.Open, database.State);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task IndependentWriterScalingRetainsEveryCommittedKey(int workers)
    {
        var directory = NewDirectory();
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            const int commitsPerWorker = 5;
            await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
            {
                for (var iteration = 0; iteration < commitsPerWorker; iteration++)
                {
                    var logicalKey = worker * 1_000 + iteration;
                    using var transaction = database.BeginTransaction();
                    transaction.Put(BitConverter.GetBytes(logicalKey), BitConverter.GetBytes(logicalKey + 1));
                    transaction.Commit();
                }
            })));

            Assert.Equal(workers * commitsPerWorker, database.Count);
            Assert.Equal((ulong)(workers * commitsPerWorker), database.CurrentCommitSequence.Value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-stress-profile-" + Guid.NewGuid().ToString("N"));
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
