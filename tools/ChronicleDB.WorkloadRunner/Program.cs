using ChronicleDB.Maintenance;
using ChronicleDB.ReferenceModel;

var seed = args.Length >= 1 && int.TryParse(args[0], out var parsedSeed) ? parsedSeed : 42;
var rounds = args.Length >= 2 && int.TryParse(args[1], out var parsedRounds) ? parsedRounds : 1_000;
var workers = args.Length >= 3 && int.TryParse(args[2], out var parsedWorkers) ? parsedWorkers : 4;
if (rounds <= 0 || workers <= 0)
{
    Console.Error.WriteLine("Usage: ChronicleDB.WorkloadRunner [seed] [positive-round-count] [positive-worker-count]");
    return 2;
}

var directory = Path.Combine(Path.GetTempPath(), "chronicle-v10-workload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
ChronicleDB.ChronicleDatabase database = null!;
var branchHandles = new Dictionary<string, ChronicleDB.ChronicleBranch>(StringComparer.Ordinal);
try
{
    var random = new Random(seed);
    var reference = new ReferenceBranchingModel();
    database = ChronicleDB.ChronicleDatabase.Open(directory);
    var branchIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
    var parentByHistory = new Dictionary<string, string?>(StringComparer.Ordinal) { ["main"] = null };
    var snapshots = new List<RetainedSnapshot>();
    var retiredHistories = new HashSet<string>(StringComparer.Ordinal);
    var nextBranch = 0;

    // Establish deterministic shared state before branching.
    for (byte key = 0; key < 16; key++)
    {
        database.Put([key], [key, 0xA5]);
        using var expected = reference.Begin("main");
        expected.Put([key], [key, 0xA5]);
        expected.Commit();
    }

    CreateBranchFromCurrent("main", "branch-A");
    CreateBranchFromCurrent("main", "branch-B");

    for (var round = 0; round < rounds; round++)
    {
        var action = random.Next(100);
        var activeHistories = GetActiveHistories();
        if (action < 58)
        {
            var history = activeHistories[random.Next(activeHistories.Count)];
            var error = RunTransaction(history, round);
            if (error is not null)
            {
                return Fail(seed, round, workers, error);
            }
        }
        else if (action < 66)
        {
            CreatePersistentSnapshot(activeHistories[random.Next(activeHistories.Count)], round);
        }
        else if (action < 71 && snapshots.Count != 0)
        {
            DeletePersistentSnapshot(random.Next(snapshots.Count));
        }
        else if (action < 77 && branchIds.Count < 8)
        {
            var parent = activeHistories[random.Next(activeHistories.Count)];
            var name = $"generated-{nextBranch++}";
            if (random.Next(4) == 0 && TryCreateBranchFromSnapshot(parent, name))
            {
                // Snapshot-derived creation exercised successfully.
            }
            else
            {
                CreateBranchFromCurrent(parent, name);
            }
        }
        else if (action < 82 && branchIds.Count > 2)
        {
            TryDeleteLeafBranch();
        }
        else if (action < 88)
        {
            var history = activeHistories[random.Next(activeHistories.Count)];
            var error = CompareRandomHistorical(history);
            if (error is not null)
            {
                return Fail(seed, round, workers, error);
            }
        }
        else if (action < 92)
        {
            _ = database.RunGarbageCollection(new GarbageCollectionOptions
            {
                RetainRecentCommits = 8,
                IncludeBranches = true,
            });
        }
        else if (action < 95)
        {
            _ = database.RunCompaction(new CompactionOptions
            {
                MaxHistoriesPerPass = 2,
                MinimumReclaimableBytes = 1,
                MaxBytesRewrittenPerPass = 32L * 1024 * 1024,
            });
        }
        else if (action < 98)
        {
            Restart();
        }
        else
        {
            var error = CompareRetainedSnapshots();
            if (error is not null)
            {
                return Fail(seed, round, workers, error);
            }
        }

        if (round % 5 == 0)
        {
            var currentError = CompareAllCurrent();
            if (currentError is not null)
            {
                return Fail(seed, round, workers, currentError);
            }
        }
        if (round % 17 == 0)
        {
            var snapshotError = CompareRetainedSnapshots();
            if (snapshotError is not null)
            {
                return Fail(seed, round, workers, snapshotError);
            }
        }
        if (round % 53 == 0)
        {
            var topologyError = ValidateTopology();
            if (topologyError is not null)
            {
                return Fail(seed, round, workers, topologyError);
            }
        }
    }

    var finalCurrentError = CompareAllCurrent();
    if (finalCurrentError is not null)
    {
        return Fail(seed, rounds, workers, finalCurrentError);
    }
    var finalSnapshotError = CompareRetainedSnapshots();
    if (finalSnapshotError is not null)
    {
        return Fail(seed, rounds, workers, finalSnapshotError);
    }

    var concurrentError = RunConcurrentPhase(database, branchHandles, workers, seed);
    if (concurrentError is not null)
    {
        return Fail(seed, rounds, workers, concurrentError);
    }

    Restart();
    finalCurrentError = CompareAllCurrent();
    if (finalCurrentError is not null)
    {
        return Fail(seed, rounds, workers, "post-concurrency restart: " + finalCurrentError);
    }

    var diagnostics = database.GetDiagnostics();
    Console.WriteLine(
        $"PASS release=v1.0 seed={seed} rounds={rounds} workers={workers} " +
        $"main-commits={database.CurrentCommitSequence.Value} branches={diagnostics.BranchCount} " +
        $"versions={diagnostics.VersionCount + diagnostics.BranchLocalVersionCount} " +
        $"snapshots={diagnostics.SnapshotCount + diagnostics.BranchSnapshotCount} " +
        $"gc-passes={diagnostics.GarbageCollectionPasses} compaction-passes={diagnostics.CompactionPasses} " +
        $"conflicts={diagnostics.ConflictAborts}");
    return 0;

    List<string> GetActiveHistories()
    {
        var result = new List<string> { "main" };
        result.AddRange(branchIds.Keys.Where(name => !retiredHistories.Contains(name)).Order(StringComparer.Ordinal));
        return result;
    }

    void CreateBranchFromCurrent(string parent, string name)
    {
        ChronicleDB.ChronicleBranch branch;
        ulong boundary;
        if (parent == "main")
        {
            boundary = database.CurrentCommitSequence.Value;
            branch = database.CreateBranch(name);
        }
        else
        {
            var parentBranch = branchHandles[parent];
            boundary = parentBranch.CurrentSequence;
            branch = parentBranch.CreateBranch(name);
        }

        reference.CreateBranch(parent, boundary, name);
        branchIds.Add(name, branch.BranchId);
        branchHandles.Add(name, branch);
        parentByHistory[name] = parent;
    }

    bool TryCreateBranchFromSnapshot(string parent, string name)
    {
        var candidates = snapshots.Where(snapshot => snapshot.History == parent).ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        var retained = candidates[random.Next(candidates.Length)];
        ChronicleDB.ChronicleBranch branch;
        if (parent == "main")
        {
            using var snapshot = database.OpenSnapshot(retained.SnapshotId);
            branch = snapshot.CreateBranch(name);
        }
        else
        {
            using var parentBranch = database.OpenBranch(branchIds[parent]);
            using var snapshot = parentBranch.OpenSnapshot(retained.SnapshotId);
            branch = snapshot.CreateBranch(name);
        }

        reference.CreateBranch(parent, retained.Sequence, name);
        branchIds.Add(name, branch.BranchId);
        branchHandles.Add(name, branch);
        parentByHistory[name] = parent;
        return true;
    }

    string? RunTransaction(string history, int round)
    {
        using var expected = reference.Begin(history);
        using var actual = history == "main"
            ? database.BeginTransaction()
            : branchHandles[history].BeginTransaction();
        if (actual.StartSequence != expected.StartSequence)
        {
            return $"{history}: start sequence mismatch";
        }

        var operationCount = random.Next(1, 6);
        for (var operation = 0; operation < operationCount; operation++)
        {
            var key = new byte[] { checked((byte)random.Next(0, 48)) };
            if (random.Next(100) < 24)
            {
                actual.Delete(key);
                expected.Delete(key);
            }
            else
            {
                var value = new byte[]
                {
                    checked((byte)random.Next(0, 256)),
                    checked((byte)random.Next(0, 256)),
                    checked((byte)(round & 0xFF)),
                    checked((byte)operation),
                };
                actual.Put(key, value);
                expected.Put(key, value);
            }
        }

        if (random.Next(100) < 12)
        {
            actual.Abort();
            expected.Abort();
            return null;
        }

        var actualConflict = false;
        try
        {
            actual.Commit();
        }
        catch (ChronicleDB.TransactionConflictException)
        {
            actualConflict = true;
        }

        var expectedConflict = false;
        try
        {
            expected.Commit();
        }
        catch (ReferenceTransactionConflictException)
        {
            expectedConflict = true;
        }

        if (actualConflict != expectedConflict)
        {
            return $"{history}: conflict outcome mismatch";
        }

        var actualSequence = history == "main"
            ? database.CurrentCommitSequence.Value
            : branchHandles[history].CurrentSequence;
        return actualSequence == reference.CurrentSequence(history)
            ? null
            : $"{history}: commit sequence mismatch actual={actualSequence} expected={reference.CurrentSequence(history)}";
    }

    void CreatePersistentSnapshot(string history, int round)
    {
        var name = $"snapshot-{round}-{snapshots.Count}";
        if (history == "main")
        {
            using var snapshot = database.CreateSnapshot(name);
            snapshots.Add(new RetainedSnapshot(history, snapshot.SnapshotId, snapshot.Sequence));
        }
        else
        {
            using var snapshot = branchHandles[history].CreateSnapshot(name);
            snapshots.Add(new RetainedSnapshot(history, snapshot.Info.SnapshotId, snapshot.Info.Sequence));
        }
    }

    void DeletePersistentSnapshot(int index)
    {
        var retained = snapshots[index];
        if (retained.History == "main")
        {
            database.DeleteSnapshot(retained.SnapshotId);
        }
        else if (branchIds.TryGetValue(retained.History, out var branchId))
        {
            branchHandles[retained.History].DeleteSnapshot(retained.SnapshotId);
        }
        else
        {
            throw new InvalidOperationException("A retained branch snapshot cannot outlive its deleted branch.");
        }
        snapshots.RemoveAt(index);
    }

    void TryDeleteLeafBranch()
    {
        var candidates = branchIds.Keys
            .Where(name => !retiredHistories.Contains(name))
            .Where(name => !parentByHistory.Any(pair => !retiredHistories.Contains(pair.Key) && pair.Value == name))
            .Where(name => snapshots.All(snapshot => snapshot.History != name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var name = candidates[random.Next(candidates.Length)];
        var id = branchIds[name];
        branchHandles[name].Dispose();
        branchHandles.Remove(name);
        database.DeleteBranch(id);
        retiredHistories.Add(name);
    }

    string? CompareRandomHistorical(string history)
    {
        var topology = database.GetHistoryTopologyDiagnostics();
        var diagnostic = history == "main"
            ? topology.Main
            : topology.Branches.Single(item => item.Name == history);
        var boundary = RandomBoundary(random, diagnostic.RetentionFloor, diagnostic.CurrentSequence);
        var key = new byte[] { checked((byte)random.Next(0, 48)) };
        var expectedFound = reference.TryRead(history, boundary, key, out var expectedValue);
        bool actualFound;
        byte[] actualValue;
        if (history == "main")
        {
            using var view = database.OpenHistoricalView(boundary);
            actualFound = view.TryGet(key, out actualValue);
        }
        else
        {
            using var view = branchHandles[history].OpenHistoricalView(boundary);
            actualFound = view.TryGet(key, out actualValue);
        }

        return Same(expectedFound, expectedValue, actualFound, actualValue)
            ? null
            : $"{history}: historical mismatch boundary={boundary} key={key[0]}";
    }

    string? CompareAllCurrent()
    {
        foreach (var history in GetActiveHistories())
        {
            var boundary = reference.CurrentSequence(history);
            for (byte key = 0; key < 48; key++)
            {
                var expectedFound = reference.TryRead(history, boundary, [key], out var expectedValue);
                byte[] actualValue;
                var actualFound = history == "main"
                    ? database.TryGet([key], out actualValue)
                    : branchHandles[history].TryGet([key], out actualValue);
                if (!Same(expectedFound, expectedValue, actualFound, actualValue))
                {
                    return $"{history}: current-state mismatch key={key}";
                }
            }
        }
        return null;
    }

    string? CompareRetainedSnapshots()
    {
        foreach (var retained in snapshots)
        {
            for (byte key = 0; key < 48; key++)
            {
                var expectedFound = reference.TryRead(retained.History, retained.Sequence, [key], out var expectedValue);
                bool actualFound;
                byte[] actualValue;
                if (retained.History == "main")
                {
                    using var snapshot = database.OpenSnapshot(retained.SnapshotId);
                    actualFound = snapshot.TryGet([key], out actualValue);
                }
                else
                {
                    using var branch = database.OpenBranch(branchIds[retained.History]);
                    using var snapshot = branch.OpenSnapshot(retained.SnapshotId);
                    actualFound = snapshot.TryGet([key], out actualValue);
                }
                if (!Same(expectedFound, expectedValue, actualFound, actualValue))
                {
                    return $"{retained.History}: retained snapshot mismatch seq={retained.Sequence} key={key}";
                }
            }
        }
        return null;
    }

    string? ValidateTopology()
    {
        var topology = database.GetHistoryTopologyDiagnostics();
        if (topology.Main.HistoryId != database.DatabaseId
            || topology.Main.CurrentSequence != database.CurrentCommitSequence.Value)
        {
            return "main topology identity/sequence mismatch";
        }

        var expectedBranches = branchIds.Keys.Where(name => !retiredHistories.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        var actualBranches = topology.Branches.Select(item => item.Name!).Order(StringComparer.Ordinal).ToArray();
        if (!expectedBranches.SequenceEqual(actualBranches, StringComparer.Ordinal))
        {
            return "history topology branch set mismatch";
        }

        foreach (var branch in topology.Branches)
        {
            if (branch.BranchId is null || !branchIds.TryGetValue(branch.Name!, out var expectedId) || branch.BranchId != expectedId)
            {
                return $"branch topology identity mismatch for {branch.Name}";
            }
            if (branch.RetentionFloor > branch.CurrentSequence)
            {
                return $"branch topology floor exceeds current sequence for {branch.Name}";
            }
        }
        return null;
    }

    void Restart()
    {
        foreach (var handle in branchHandles.Values)
        {
            handle.Dispose();
        }
        branchHandles.Clear();
        database.Dispose();
        database = ChronicleDB.ChronicleDatabase.Open(directory);
        foreach (var (name, id) in branchIds)
        {
            if (!retiredHistories.Contains(name))
            {
                branchHandles.Add(name, database.OpenBranch(id));
            }
        }
    }
}
finally
{
    foreach (var branch in branchHandles.Values)
    {
        branch.Dispose();
    }
    database?.Dispose();
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

static string? RunConcurrentPhase(
    ChronicleDB.ChronicleDatabase database,
    IReadOnlyDictionary<string, ChronicleDB.ChronicleBranch> branches,
    int workers,
    int seed)
{
    var activeBranches = branches.Values.Take(2).ToArray();
    var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
    {
        var random = new Random(HashCode.Combine(seed, worker));
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var target = activeBranches.Length == 0 || iteration % 3 == 0
                ? null
                : activeBranches[(worker + iteration) % activeBranches.Length];
            using var transaction = target is null ? database.BeginTransaction() : target.BeginTransaction();
            var key = BitConverter.GetBytes(100_000 + worker * 100 + iteration);
            transaction.Put(key, BitConverter.GetBytes(random.Next()));
            transaction.Commit();
        }
    })).ToArray();

    try
    {
        Task.WaitAll(tasks);
        return null;
    }
    catch (AggregateException exception)
    {
        return "concurrent multi-history phase failed: " + exception.Flatten().InnerExceptions[0].Message;
    }
}

static ulong RandomBoundary(Random random, ulong floor, ulong current)
{
    if (floor > current)
    {
        throw new InvalidOperationException("A retention floor cannot exceed current history.");
    }
    if (floor == current)
    {
        return floor;
    }
    if (current <= long.MaxValue - 1UL)
    {
        return checked((ulong)random.NextInt64(checked((long)floor), checked((long)current + 1)));
    }
    return current;
}

static bool Same(bool expectedFound, byte[] expected, bool actualFound, byte[] actual)
    => expectedFound == actualFound && (!expectedFound || expected.AsSpan().SequenceEqual(actual));

static int Fail(int seed, int round, int workers, string message)
{
    Console.Error.WriteLine($"FAIL release=v1.0 seed={seed} round={round} workers={workers}: {message}");
    return 1;
}

sealed record RetainedSnapshot(string History, Guid SnapshotId, ulong Sequence);
