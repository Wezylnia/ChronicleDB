using ChronicleDB.ReferenceModel;

var seed = args.Length >= 1 && int.TryParse(args[0], out var parsedSeed) ? parsedSeed : 42;
var rounds = args.Length >= 2 && int.TryParse(args[1], out var parsedRounds) ? parsedRounds : 1_000;
var workers = args.Length >= 3 && int.TryParse(args[2], out var parsedWorkers) ? parsedWorkers : 4;
if (rounds <= 0 || workers <= 0)
{
    Console.Error.WriteLine("Usage: ChronicleDB.WorkloadRunner [seed] [positive-round-count] [positive-worker-count]");
    return 2;
}

var directory = Path.Combine(Path.GetTempPath(), "chronicle-workload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
ChronicleDB.ChronicleDatabase? database = null;
try
{
    var random = new Random(seed);
    var model = new ReferenceMvccModel();
    database = ChronicleDB.ChronicleDatabase.Open(directory);
    var snapshotNames = new List<string>();

    for (var round = 0; round < rounds; round++)
    {
        var action = random.Next(100);
        if (action < 68)
        {
            var result = RunTransactionRound(database, model, random, round);
            if (result is not null)
            {
                return Fail(seed, round, workers, result);
            }
        }
        else if (action < 78)
        {
            var name = $"snapshot-{round}";
            using var engineSnapshot = database.CreateSnapshot(name);
            var referenceSnapshot = model.CreateSnapshot(name);
            if (engineSnapshot.Sequence != referenceSnapshot.Sequence)
            {
                return Fail(seed, round, workers, "snapshot sequence mismatch");
            }

            snapshotNames.Add(name);
        }
        else if (action < 83 && snapshotNames.Count != 0)
        {
            var index = random.Next(snapshotNames.Count);
            var name = snapshotNames[index];
            snapshotNames.RemoveAt(index);
            var snapshot = database.ListSnapshots().Single(item => item.Name == name);
            database.DeleteSnapshot(snapshot.SnapshotId);
            model.DeleteSnapshot(name);
        }
        else if (action < 94)
        {
            var boundary = NextHistoricalBoundary(random, database.CurrentCommitSequence.Value);
            using var historical = database.OpenHistoricalView(boundary);
            var key = new byte[] { checked((byte)random.Next(0, 32)) };
            var engineFound = historical.TryGet(key, out var engineValue);
            var referenceFound = model.TryReadHistorical(key, boundary, out var referenceValue);
            if (engineFound != referenceFound || !engineValue.AsSpan().SequenceEqual(referenceValue))
            {
                return Fail(seed, round, workers, $"historical mismatch boundary={boundary} key={key[0]}");
            }
        }
        else
        {
            database.Dispose();
            database = ChronicleDB.ChronicleDatabase.Open(directory);
        }

        var stateError = CompareCurrentState(database, model);
        if (stateError is not null)
        {
            return Fail(seed, round, workers, stateError);
        }

        if (!database.ListSnapshots().Select(item => item.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(snapshotNames.Order(StringComparer.Ordinal)))
        {
            return Fail(seed, round, workers, "persistent snapshot registry mismatch");
        }
    }

    var concurrentError = RunConcurrentPhase(database, workers, seed);
    if (concurrentError is not null)
    {
        return Fail(seed, rounds, workers, concurrentError);
    }

    var diagnostics = database.GetDiagnostics();
    Console.WriteLine(
        $"PASS seed={seed} rounds={rounds} workers={workers} commits={database.CurrentCommitSequence.Value} " +
        $"versions={diagnostics.VersionCount} snapshots={diagnostics.SnapshotCount} " +
        $"conflicts={diagnostics.ConflictAborts} commit-contention={diagnostics.CommitSerializationContention}");
    return 0;
}
finally
{
    database?.Dispose();
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

static ulong NextHistoricalBoundary(Random random, ulong current)
{
    if (current <= long.MaxValue - 1UL)
    {
        return checked((ulong)random.NextInt64(0, checked((long)current + 1)));
    }

    Span<byte> bytes = stackalloc byte[sizeof(ulong)];
    if (current == ulong.MaxValue)
    {
        random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    var modulus = current + 1;
    var rejectionLimit = ulong.MaxValue - (ulong.MaxValue % modulus);
    ulong candidate;
    do
    {
        random.NextBytes(bytes);
        candidate = BitConverter.ToUInt64(bytes);
    }
    while (candidate >= rejectionLimit);

    return candidate % modulus;
}

static string? RunTransactionRound(
    ChronicleDB.ChronicleDatabase database,
    ReferenceMvccModel model,
    Random random,
    int round)
{
    using var engineTransaction = database.BeginTransaction();
    using var referenceTransaction = model.BeginTransaction();
    if (engineTransaction.StartSequence != referenceTransaction.StartSequence)
    {
        return "start sequence mismatch";
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
        return $"conflict outcome mismatch in round {round}";
    }

    return database.CurrentCommitSequence.Value == model.CurrentCommitSequence
        ? null
        : "commit sequence mismatch";
}

static string? CompareCurrentState(
    ChronicleDB.ChronicleDatabase database,
    ReferenceMvccModel model)
{
    using var referenceView = model.BeginTransaction();
    for (var keyValue = 0; keyValue < 32; keyValue++)
    {
        var key = new byte[] { checked((byte)keyValue) };
        var engineFound = database.TryGet(key, out var engineValue);
        var referenceFound = referenceView.TryGet(key, out var referenceValue);
        if (engineFound != referenceFound || !engineValue.AsSpan().SequenceEqual(referenceValue))
        {
            return $"state mismatch for key {keyValue}";
        }
    }

    referenceView.Abort();
    return null;
}

static string? RunConcurrentPhase(
    ChronicleDB.ChronicleDatabase database,
    int workers,
    int seed)
{
    var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
    {
        var random = new Random(HashCode.Combine(seed, worker));
        for (var iteration = 0; iteration < 20; iteration++)
        {
            using var transaction = database.BeginTransaction();
            var key = BitConverter.GetBytes(10_000 + worker * 100 + iteration);
            transaction.Put(key, BitConverter.GetBytes(random.Next()));
            transaction.Commit();
        }
    })).ToArray();

    try
    {
        Task.WaitAll(tasks);
    }
    catch (AggregateException exception)
    {
        return "concurrent phase failed: " + exception.Flatten().InnerExceptions[0].Message;
    }

    return null;
}

static int Fail(int seed, int round, int workers, string message)
{
    Console.Error.WriteLine($"FAIL seed={seed} round={round} workers={workers}: {message}");
    return 1;
}
