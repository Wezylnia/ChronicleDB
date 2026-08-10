using ChronicleDB.ReferenceModel;

namespace ChronicleDB.CorrectnessTests;

public sealed class MvccDifferentialTests
{
    [Fact]
    public void DeterministicGeneratedHistoriesMatchReferenceModel()
    {
        for (var seed = 1; seed <= 24; seed++)
        {
            RunSeed(seed);
        }
    }

    private static void RunSeed(int seed)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "chronicle-mvcc-differential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var random = new Random(seed);
            var model = new ReferenceMvccModel();
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);

            for (var round = 0; round < 120; round++)
            {
                using var engineTransaction = database.BeginTransaction();
                using var referenceTransaction = model.BeginTransaction();
                Assert.Equal(referenceTransaction.StartSequence, engineTransaction.StartSequence);

                var operationCount = random.Next(1, 5);
                for (var operation = 0; operation < operationCount; operation++)
                {
                    ApplyRandomLocalMutation(random, engineTransaction, referenceTransaction);
                    CompareRandomRead(random, engineTransaction, referenceTransaction);
                }

                if (random.Next(100) < 40)
                {
                    using var engineWinner = database.BeginTransaction();
                    using var referenceWinner = model.BeginTransaction();
                    Assert.Equal(referenceWinner.StartSequence, engineWinner.StartSequence);
                    ApplyRandomLocalMutation(random, engineWinner, referenceWinner);
                    engineWinner.Commit();
                    var referenceWinnerSequence = referenceWinner.Commit();
                    Assert.Equal(referenceWinnerSequence, engineWinner.CommitSequence!.Value);

                    // The older transaction must keep reading its original snapshot plus
                    // its own local writes after a concurrent commit.
                    CompareRandomRead(random, engineTransaction, referenceTransaction);
                }

                var engineConflict = false;
                try
                {
                    engineTransaction.Commit();
                }
                catch (ChronicleDB.TransactionConflictException)
                {
                    engineConflict = true;
                }

                var referenceConflict = false;
                try
                {
                    referenceTransaction.Commit();
                }
                catch (ReferenceTransactionConflictException)
                {
                    referenceConflict = true;
                }

                Assert.Equal(referenceConflict, engineConflict);
                Assert.Equal(model.CurrentCommitSequence, database.CurrentCommitSequence.Value);
                CompareCurrentState(database, model);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void ApplyRandomLocalMutation(
        Random random,
        ChronicleDB.ChronicleTransaction engine,
        ReferenceTransaction reference)
    {
        var key = new byte[] { checked((byte)random.Next(0, 12)) };
        if (random.Next(100) < 25)
        {
            engine.Delete(key);
            reference.Delete(key);
            return;
        }

        var value = new byte[]
        {
            checked((byte)random.Next(0, 256)),
            checked((byte)random.Next(0, 256))
        };
        engine.Put(key, value);
        reference.Put(key, value);
    }

    private static void CompareRandomRead(
        Random random,
        ChronicleDB.ChronicleTransaction engine,
        ReferenceTransaction reference)
    {
        var key = new byte[] { checked((byte)random.Next(0, 12)) };
        var engineFound = engine.TryGet(key, out var engineValue);
        var referenceFound = reference.TryGet(key, out var referenceValue);
        Assert.Equal(referenceFound, engineFound);
        Assert.Equal(referenceValue, engineValue);
    }

    private static void CompareCurrentState(
        ChronicleDB.ChronicleDatabase database,
        ReferenceMvccModel model)
    {
        using var reference = model.BeginTransaction();
        for (var keyValue = 0; keyValue < 12; keyValue++)
        {
            var key = new byte[] { checked((byte)keyValue) };
            var engineFound = database.TryGet(key, out var engineValue);
            var referenceFound = reference.TryGet(key, out var referenceValue);
            Assert.Equal(referenceFound, engineFound);
            Assert.Equal(referenceValue, engineValue);
        }

        reference.Abort();
    }
}
