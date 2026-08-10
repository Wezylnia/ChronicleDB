using ChronicleDB.ReferenceModel;

var seed = args.Length >= 1 && int.TryParse(args[0], out var parsedSeed) ? parsedSeed : 42;
var rounds = args.Length >= 2 && int.TryParse(args[1], out var parsedRounds) ? parsedRounds : 1_000;
if (rounds <= 0)
{
    Console.Error.WriteLine("Usage: ChronicleDB.WorkloadRunner [seed] [positive-round-count]");
    return 2;
}

var directory = Path.Combine(
    Path.GetTempPath(),
    "chronicle-workload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var random = new Random(seed);
    var model = new ReferenceMvccModel();
    using var database = ChronicleDB.ChronicleDatabase.Open(directory);

    for (var round = 0; round < rounds; round++)
    {
        using var engineTransaction = database.BeginTransaction();
        using var referenceTransaction = model.BeginTransaction();
        if (engineTransaction.StartSequence != referenceTransaction.StartSequence)
        {
            return Fail($"round {round}: start sequence mismatch");
        }

        var operationCount = random.Next(1, 6);
        for (var operation = 0; operation < operationCount; operation++)
        {
            var key = new byte[] { checked((byte)random.Next(0, 32)) };
            if (random.Next(100) < 25)
            {
                engineTransaction.Delete(key);
                referenceTransaction.Delete(key);
            }
            else
            {
                var value = new byte[]
                {
                    checked((byte)random.Next(0, 256)),
                    checked((byte)random.Next(0, 256)),
                    checked((byte)random.Next(0, 256))
                };
                engineTransaction.Put(key, value);
                referenceTransaction.Put(key, value);
            }
        }

        if (random.Next(100) < 35)
        {
            using var engineConcurrent = database.BeginTransaction();
            using var referenceConcurrent = model.BeginTransaction();
            var key = new byte[] { checked((byte)random.Next(0, 32)) };
            var value = new byte[] { checked((byte)random.Next(0, 256)) };
            engineConcurrent.Put(key, value);
            referenceConcurrent.Put(key, value);
            engineConcurrent.Commit();
            referenceConcurrent.Commit();
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

        if (engineConflict != referenceConflict)
        {
            return Fail($"round {round}: conflict outcome mismatch");
        }

        if (database.CurrentCommitSequence.Value != model.CurrentCommitSequence)
        {
            return Fail($"round {round}: commit sequence mismatch");
        }

        using var referenceView = model.BeginTransaction();
        for (var keyValue = 0; keyValue < 32; keyValue++)
        {
            var key = new byte[] { checked((byte)keyValue) };
            var engineFound = database.TryGet(key, out var engineValue);
            var referenceFound = referenceView.TryGet(key, out var referenceValue);
            if (engineFound != referenceFound || !engineValue.AsSpan().SequenceEqual(referenceValue))
            {
                return Fail($"round {round}: state mismatch for key {keyValue}");
            }
        }

        referenceView.Abort();
    }

    Console.WriteLine(
        $"PASS seed={seed} rounds={rounds} commits={database.CurrentCommitSequence.Value}");
    return 0;
}
finally
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine("FAIL " + message);
    return 1;
}
