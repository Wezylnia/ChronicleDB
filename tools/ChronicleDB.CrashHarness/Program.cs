using ChronicleDB;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Transactions.Faults;

if (args.Length == 0 || args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    var iterations = args.Length >= 2 && int.TryParse(args[1], out var parsedIterations)
        ? parsedIterations
        : 1;
    if (iterations <= 0)
    {
        Console.Error.WriteLine("Usage: run [positive-iterations] | child <directory> <fault-point>");
        return 2;
    }

    return await RunHarnessAsync(iterations);
}

if (args.Length == 3 && args[0].Equals("child", StringComparison.OrdinalIgnoreCase))
{
    return RunChild(args[1], args[2]);
}

Console.Error.WriteLine("Usage: run [positive-iterations] | child <directory> <fault-point>");
return 2;

static async Task<int> RunHarnessAsync(int iterations)
{
    var scenarios = Enum.GetValues<TransactionFaultPoint>()
        .Select(point => point.ToString())
        .Concat(Enum.GetValues<TransactionFaultPoint>()
            .Select(point => CrashScenario.BranchCommitPrefix + point))
        .Concat(
        [
            CrashScenario.PhysicalPage,
            CrashScenario.SnapshotBeforeWrite,
            CrashScenario.SnapshotAfterWrite,
            CrashScenario.SnapshotBeforeFlush,
            CrashScenario.SnapshotAfterFlush,
            CrashScenario.SnapshotAfterAcknowledgement,
            CrashScenario.SnapshotDeleteBeforeWrite,
            CrashScenario.SnapshotDeleteAfterWrite,
            CrashScenario.SnapshotDeleteBeforeFlush,
            CrashScenario.SnapshotDeleteAfterFlush,
            CrashScenario.SnapshotThenLaterPhysicalCrash,
            CrashScenario.BranchCreateAfterIntent,
            CrashScenario.BranchCreateAfterBaseRoot,
            CrashScenario.BranchCreateAfterActivation,
            CrashScenario.BranchDeleteAfterIntent,
            CrashScenario.BranchDeleteAfterBaseRootDelete,
            CrashScenario.BranchSnapshotAfterFlush,
            CrashScenario.GarbageCollectionDuringCheckpointWrite,
            CrashScenario.GarbageCollectionBeforeWalReset,
            CrashScenario.GarbageCollectionAfterWalReset,
            CrashScenario.CompactionDuringOutputWrite,
            CrashScenario.CompactionBeforePublish,
            CrashScenario.CompactionAfterPublish
        ])
        .ToArray();
    var failures = 0;
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        foreach (var scenario in scenarios)
        {
            var directory = Path.Combine(Path.GetTempPath(), "chronicle-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var process = StartChild(directory, scenario);
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    Console.Error.WriteLine($"{scenario}: child exited normally; the configured crash point was not reached.");
                    failures++;
                    continue;
                }

                var valid = scenario.StartsWith("Snapshot", StringComparison.Ordinal)
                    ? ValidateSnapshotScenario(directory, scenario)
                    : scenario.StartsWith(CrashScenario.BranchCommitPrefix, StringComparison.Ordinal)
                        ? ValidateBranchTransactionScenario(directory, scenario)
                        : scenario.StartsWith("BranchCreate", StringComparison.Ordinal)
                            ? ValidateBranchCreateScenario(directory, scenario)
                            : scenario.StartsWith("BranchDelete", StringComparison.Ordinal)
                                ? ValidateBranchDeleteScenario(directory, scenario)
                                : scenario.StartsWith("BranchSnapshot", StringComparison.Ordinal)
                                    ? ValidateBranchSnapshotScenario(directory, scenario)
                                    : scenario.StartsWith("GarbageCollection", StringComparison.Ordinal)
                                        || scenario.StartsWith("Compaction", StringComparison.Ordinal)
                                        ? ValidateMaintenanceScenario(directory, scenario)
                                        : ValidateTransactionScenario(directory, scenario);
                if (!valid)
                {
                    failures++;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"iteration={iteration} {scenario}: validation failed with {exception.GetType().Name}: {exception.Message}");
                failures++;
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    await Task.CompletedTask;
    return failures == 0 ? 0 : 1;
}

static System.Diagnostics.Process StartChild(string directory, string scenario)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
    if (processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(typeof(CrashInjector).Assembly.Location);
    }

    startInfo.ArgumentList.Add("child");
    startInfo.ArgumentList.Add(directory);
    startInfo.ArgumentList.Add(scenario);
    return System.Diagnostics.Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start ChronicleDB crash-harness child process.");
}

static bool ValidateTransactionScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    var firstFound = database.TryGet([1], out var first);
    var secondFound = database.TryGet([2], out var second);
    var complete = firstFound
        && secondFound
        && first.SequenceEqual(new byte[] { 11 })
        && second.SequenceEqual(new byte[] { 22 });
    var noneVisible = !firstFound && !secondFound;
    var atomic = noneVisible || complete;
    var durableExpectation = scenario switch
    {
        nameof(TransactionFaultPoint.BeforeWalAppend) => noneVisible,
        nameof(TransactionFaultPoint.AfterWalAppend)
            or nameof(TransactionFaultPoint.BeforeWalFlush) => true,
        _ => complete
    };
    var valid = atomic && durableExpectation;
    Console.WriteLine(
        $"{scenario}: {(valid ? "PASS" : "FAIL")} " +
        $"atomic={atomic} outcome={(complete ? "committed" : noneVisible ? "not-visible" : "partial")}");
    return valid;
}

static bool ValidateBranchTransactionScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    using var branch = database.OpenBranch("branch-crash");
    var firstFound = branch.TryGet([1], out var first);
    var secondFound = branch.TryGet([2], out var second);
    var complete = firstFound
        && secondFound
        && first.SequenceEqual(new byte[] { 99 })
        && second.SequenceEqual(new byte[] { 22 });
    var noneVisible = firstFound
        && first.SequenceEqual(new byte[] { 10 })
        && !secondFound;
    var atomic = noneVisible || complete;

    var pointName = scenario[CrashScenario.BranchCommitPrefix.Length..];
    if (!Enum.TryParse<TransactionFaultPoint>(pointName, ignoreCase: true, out var point))
    {
        Console.Error.WriteLine($"{scenario}: invalid branch fault point.");
        return false;
    }

    var durableExpectation = point switch
    {
        TransactionFaultPoint.BeforeWalAppend => noneVisible,
        TransactionFaultPoint.AfterWalAppend or TransactionFaultPoint.BeforeWalFlush => true,
        _ => complete,
    };
    var valid = atomic && durableExpectation;
    Console.WriteLine(
        $"{scenario}: {(valid ? "PASS" : "FAIL")} " +
        $"atomic={atomic} outcome={(complete ? "committed" : noneVisible ? "not-visible" : "partial")}");
    return valid;
}

static bool ValidateBranchCreateScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    var branches = database.ListBranches();
    var shouldExist = scenario == CrashScenario.BranchCreateAfterActivation;
    if (!shouldExist)
    {
        var validAbsent = branches.Count == 0;
        Console.WriteLine($"{scenario}: {(validAbsent ? "PASS" : "FAIL")} branches={branches.Count}");
        return validAbsent;
    }

    if (branches.Count != 1 || !string.Equals(branches[0].Name, "lifecycle", StringComparison.Ordinal))
    {
        Console.WriteLine($"{scenario}: FAIL branches={branches.Count}");
        return false;
    }

    using var branch = database.OpenBranch("lifecycle");
    var inherited = branch.TryGet([1], out var value) && value.SequenceEqual(new byte[] { 10 });
    Console.WriteLine($"{scenario}: {(inherited ? "PASS" : "FAIL")} inherited={inherited}");
    return inherited;
}

static bool ValidateBranchDeleteScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    var branches = database.ListBranches();
    var valid = branches.Count == 0;
    Console.WriteLine($"{scenario}: {(valid ? "PASS" : "FAIL")} branches={branches.Count}");
    return valid;
}

static bool ValidateBranchSnapshotScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    using var branch = database.OpenBranch("snapshot-branch");
    var snapshots = branch.ListSnapshots();
    if (snapshots.Count != 1)
    {
        Console.WriteLine($"{scenario}: FAIL snapshots={snapshots.Count}");
        return false;
    }

    using var snapshot = branch.OpenSnapshot(snapshots[0].SnapshotId);
    var valid = snapshot.TryGet([1], out var value) && value.SequenceEqual(new byte[] { 55 });
    Console.WriteLine($"{scenario}: {(valid ? "PASS" : "FAIL")} snapshots={snapshots.Count}");
    return valid;
}

static bool ValidateMaintenanceScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    using var snapshot = database.OpenSnapshot("maintenance-stable");
    var snapshotCorrect = snapshot.TryGet([1], out var historical)
        && historical.SequenceEqual(new byte[] { 10 });
    var currentCorrect = database.TryGet([1], out var current)
        && current.SequenceEqual(new byte[] { 39 });
    var valid = snapshotCorrect && currentCorrect;
    Console.WriteLine(
        $"{scenario}: {(valid ? "PASS" : "FAIL")} snapshot={snapshotCorrect} current={currentCorrect}");
    return valid;
}

static bool ValidateSnapshotScenario(string directory, string scenario)
{
    using var database = ChronicleDatabase.Open(directory);
    if (scenario == CrashScenario.SnapshotThenLaterPhysicalCrash)
    {
        using var snapshot = database.OpenSnapshot("stable-before-write");
        var snapshotCorrect = snapshot.TryGet([1], out var historical)
            && historical.SequenceEqual(new byte[] { 10 });
        var currentCorrect = database.TryGet([1], out var current)
            && current.SequenceEqual(new byte[] { 20 });
        var validLaterCrash = snapshotCorrect && currentCorrect;
        Console.WriteLine(
            $"{scenario}: {(validLaterCrash ? "PASS" : "FAIL")} snapshot={snapshotCorrect} current={currentCorrect}");
        return validLaterCrash;
    }

    var snapshots = database.ListSnapshots();
    var countValid = scenario switch
    {
        CrashScenario.SnapshotBeforeWrite => snapshots.Count == 0,
        CrashScenario.SnapshotAfterFlush or CrashScenario.SnapshotAfterAcknowledgement => snapshots.Count == 1,
        CrashScenario.SnapshotDeleteBeforeWrite => snapshots.Count == 1,
        CrashScenario.SnapshotDeleteAfterFlush => snapshots.Count == 0,
        CrashScenario.SnapshotDeleteAfterWrite or CrashScenario.SnapshotDeleteBeforeFlush => snapshots.Count is 0 or 1,
        _ => snapshots.Count is 0 or 1
    };
    var contentsValid = true;
    if (snapshots.Count == 1)
    {
        using var snapshot = database.OpenSnapshot(snapshots[0].SnapshotId);
        contentsValid = snapshot.TryGet([1], out var historical)
            && historical.SequenceEqual(new byte[] { 10 });
    }

    var valid = countValid && contentsValid;
    Console.WriteLine(
        $"{scenario}: {(valid ? "PASS" : "FAIL")} snapshots={snapshots.Count} contents={contentsValid}");
    return valid;
}

static int RunChild(string directory, string scenario)
{
    if (scenario.StartsWith(CrashScenario.BranchCommitPrefix, StringComparison.Ordinal))
    {
        var pointName = scenario[CrashScenario.BranchCommitPrefix.Length..];
        if (!Enum.TryParse<TransactionFaultPoint>(pointName, ignoreCase: true, out var branchPoint))
        {
            return 2;
        }

        using (var setup = ChronicleDatabase.Open(directory))
        {
            setup.Put([1], [10]);
            using var created = setup.CreateBranch("branch-crash");
        }

        using var database = ChronicleDatabase.Open(directory, faultInjector: new CrashInjector(branchPoint));
        using var branch = database.OpenBranch("branch-crash");
        using var transaction = branch.BeginTransaction();
        transaction.Put([1], [99]);
        transaction.Put([2], [22]);
        transaction.Commit();
        return 0;
    }

    if (scenario is CrashScenario.BranchCreateAfterIntent
        or CrashScenario.BranchCreateAfterBaseRoot
        or CrashScenario.BranchCreateAfterActivation)
    {
        using (var setup = ChronicleDatabase.Open(directory))
        {
            setup.Put([1], [10]);
        }

        IStorageFaultInjector injector = scenario switch
        {
            CrashScenario.BranchCreateAfterIntent
                => new NthFailFastStorageInjector(StorageFaultPoint.AfterBranchMetadataFlush, 1),
            CrashScenario.BranchCreateAfterBaseRoot
                => new NthFailFastStorageInjector(StorageFaultPoint.AfterHistoryRootFlush, 1),
            CrashScenario.BranchCreateAfterActivation
                => new NthFailFastStorageInjector(StorageFaultPoint.AfterBranchMetadataFlush, 2),
            _ => throw new InvalidOperationException("Unknown branch-create crash scenario."),
        };
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = injector });
        using var branch = database.CreateBranch("lifecycle");
        return 0;
    }

    if (scenario is CrashScenario.BranchDeleteAfterIntent
        or CrashScenario.BranchDeleteAfterBaseRootDelete)
    {
        using (var setup = ChronicleDatabase.Open(directory))
        {
            setup.Put([1], [10]);
            using var created = setup.CreateBranch("delete-me");
        }

        var target = scenario == CrashScenario.BranchDeleteAfterIntent
            ? StorageFaultPoint.AfterBranchMetadataFlush
            : StorageFaultPoint.AfterHistoryRootFlush;
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = new NthFailFastStorageInjector(target, 1) });
        database.DeleteBranch("delete-me");
        return 0;
    }

    if (scenario == CrashScenario.BranchSnapshotAfterFlush)
    {
        using (var setup = ChronicleDatabase.Open(directory))
        {
            setup.Put([1], [10]);
            using var branch = setup.CreateBranch("snapshot-branch");
            branch.Put([1], [55]);
        }

        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions
            {
                FaultInjector = new NthFailFastStorageInjector(StorageFaultPoint.AfterSnapshotFlush, 1),
            });
        using var branchHandle = database.OpenBranch("snapshot-branch");
        using var snapshotHandle = branchHandle.CreateSnapshot("branch-stable");
        return 0;
    }

    if (scenario is CrashScenario.GarbageCollectionDuringCheckpointWrite
        or CrashScenario.GarbageCollectionBeforeWalReset
        or CrashScenario.GarbageCollectionAfterWalReset
        or CrashScenario.CompactionDuringOutputWrite
        or CrashScenario.CompactionBeforePublish
        or CrashScenario.CompactionAfterPublish)
    {
        using (var setup = ChronicleDatabase.Open(directory))
        {
            setup.Put([1], [10]);
            using (setup.CreateSnapshot("maintenance-stable"))
            {
            }
            for (byte value = 11; value <= 39; value++)
            {
                setup.Put([1], [value]);
            }
        }

        var target = scenario switch
        {
            CrashScenario.GarbageCollectionDuringCheckpointWrite => StorageFaultPoint.AfterHistoryCheckpointRecordWrite,
            CrashScenario.GarbageCollectionBeforeWalReset => StorageFaultPoint.BeforeHistoryWalReset,
            CrashScenario.GarbageCollectionAfterWalReset => StorageFaultPoint.AfterHistoryWalReset,
            CrashScenario.CompactionDuringOutputWrite => StorageFaultPoint.AfterPageWrite,
            CrashScenario.CompactionBeforePublish => StorageFaultPoint.BeforeCompactionPublish,
            CrashScenario.CompactionAfterPublish => StorageFaultPoint.AfterCompactionPublish,
            _ => throw new InvalidOperationException("Unknown maintenance crash scenario."),
        };
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = new FailFastStorageInjector(target) });
        if (scenario.StartsWith("GarbageCollection", StringComparison.Ordinal))
        {
            _ = database.RunGarbageCollection(new ChronicleDB.Maintenance.GarbageCollectionOptions
            {
                RetainRecentCommits = 2,
            });
        }
        else
        {
            _ = database.RunCompaction(new ChronicleDB.Maintenance.CompactionOptions
            {
                MaxHistoriesPerPass = 1,
                MinimumReclaimableBytes = 1,
            });
        }
        return 0;
    }

    if (scenario == CrashScenario.PhysicalPage)
    {
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = new PhysicalCrashInjector() });
        using var transaction = database.BeginTransaction();
        transaction.Put([1], [11]);
        transaction.Put([2], [22]);
        transaction.Commit();
        return 0;
    }

    if (scenario == CrashScenario.SnapshotThenLaterPhysicalCrash)
    {
        var injector = new ArmedPhysicalCrashInjector();
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = injector });
        database.Put([1], [10]);
        using (database.CreateSnapshot("stable-before-write"))
        {
        }

        injector.Arm();
        database.Put([1], [20]);
        return 0;
    }

    if (scenario == CrashScenario.SnapshotAfterAcknowledgement)
    {
        using var database = ChronicleDatabase.Open(directory);
        database.Put([1], [10]);
        using var snapshot = database.CreateSnapshot("crash-snapshot");
        Environment.FailFast("Injected crash immediately after snapshot acknowledgement.");
    }

    if (TrySnapshotDeleteFaultPoint(scenario, out var deletePoint))
    {
        var injector = new ArmedSnapshotCrashInjector(deletePoint);
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = injector });
        database.Put([1], [10]);
        Guid snapshotId;
        using (var snapshot = database.CreateSnapshot("crash-snapshot"))
        {
            snapshotId = snapshot.SnapshotId;
        }

        injector.Arm();
        database.DeleteSnapshot(snapshotId);
        return 0;
    }

    if (TrySnapshotFaultPoint(scenario, out var storagePoint))
    {
        using var database = ChronicleDatabase.Open(
            directory,
            new StorageOptions { FaultInjector = new SnapshotCrashInjector(storagePoint) });
        database.Put([1], [10]);
        using var snapshot = database.CreateSnapshot("crash-snapshot");
        return 0;
    }

    if (!Enum.TryParse<TransactionFaultPoint>(scenario, ignoreCase: true, out var point))
    {
        return 2;
    }

    using var transactionDatabase = ChronicleDatabase.Open(directory, faultInjector: new CrashInjector(point));
    using var transactionToCrash = transactionDatabase.BeginTransaction();
    transactionToCrash.Put([1], [11]);
    transactionToCrash.Put([2], [22]);
    transactionToCrash.Commit();
    return 0;
}

static bool TrySnapshotFaultPoint(string scenario, out StorageFaultPoint point)
{
    point = scenario switch
    {
        CrashScenario.SnapshotBeforeWrite => StorageFaultPoint.BeforeSnapshotRecordWrite,
        CrashScenario.SnapshotAfterWrite => StorageFaultPoint.AfterSnapshotRecordWrite,
        CrashScenario.SnapshotBeforeFlush => StorageFaultPoint.BeforeSnapshotFlush,
        CrashScenario.SnapshotAfterFlush => StorageFaultPoint.AfterSnapshotFlush,
        _ => default
    };
    return scenario is CrashScenario.SnapshotBeforeWrite
        or CrashScenario.SnapshotAfterWrite
        or CrashScenario.SnapshotBeforeFlush
        or CrashScenario.SnapshotAfterFlush;
}

static bool TrySnapshotDeleteFaultPoint(string scenario, out StorageFaultPoint point)
{
    point = scenario switch
    {
        CrashScenario.SnapshotDeleteBeforeWrite => StorageFaultPoint.BeforeSnapshotRecordWrite,
        CrashScenario.SnapshotDeleteAfterWrite => StorageFaultPoint.AfterSnapshotRecordWrite,
        CrashScenario.SnapshotDeleteBeforeFlush => StorageFaultPoint.BeforeSnapshotFlush,
        CrashScenario.SnapshotDeleteAfterFlush => StorageFaultPoint.AfterSnapshotFlush,
        _ => default
    };
    return scenario is CrashScenario.SnapshotDeleteBeforeWrite
        or CrashScenario.SnapshotDeleteAfterWrite
        or CrashScenario.SnapshotDeleteBeforeFlush
        or CrashScenario.SnapshotDeleteAfterFlush;
}

file sealed class CrashInjector(TransactionFaultPoint target) : ITransactionFaultInjector
{
    public void Hit(TransactionFaultPoint point)
    {
        if (point == target)
        {
            Environment.FailFast($"Injected crash at {point}.");
        }
    }
}

file sealed class PhysicalCrashInjector : IStorageFaultInjector
{
    private int _pageWrites;

    public void Hit(StorageFaultPoint point, PageId pageId)
    {
        if (point == StorageFaultPoint.AfterPageWrite
            && Interlocked.Increment(ref _pageWrites) == 1)
        {
            Environment.FailFast($"Injected crash after physical page {pageId.Value}.");
        }
    }
}

file sealed class ArmedPhysicalCrashInjector : IStorageFaultInjector
{
    private int _armed;
    private int _pageWrites;

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Hit(StorageFaultPoint point, PageId pageId)
    {
        if (Volatile.Read(ref _armed) != 0
            && point == StorageFaultPoint.AfterPageWrite
            && Interlocked.Increment(ref _pageWrites) == 1)
        {
            Environment.FailFast($"Injected later crash after physical page {pageId.Value}.");
        }
    }
}

file sealed class ArmedSnapshotCrashInjector(StorageFaultPoint target) : IStorageFaultInjector
{
    private int _armed;

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Hit(StorageFaultPoint point, PageId pageId)
    {
        if (Volatile.Read(ref _armed) != 0 && point == target)
        {
            Environment.FailFast($"Injected snapshot-delete crash at {point}.");
        }
    }
}

file sealed class SnapshotCrashInjector(StorageFaultPoint target) : IStorageFaultInjector
{
    public void Hit(StorageFaultPoint point, PageId pageId)
    {
        if (point == target)
        {
            Environment.FailFast($"Injected snapshot crash at {point}.");
        }
    }
}

file sealed class NthFailFastStorageInjector(StorageFaultPoint target, int occurrence) : IStorageFaultInjector
{
    private int _hits;

    public void Hit(StorageFaultPoint point, PageId pageId)
    {
        if (point == target && Interlocked.Increment(ref _hits) == occurrence)
        {
            Environment.FailFast($"Injected lifecycle crash at {point} occurrence {occurrence}.");
        }
    }
}

file sealed class FailFastStorageInjector(StorageFaultPoint target) : IStorageFaultInjector
{
    public void Hit(StorageFaultPoint point, PageId pageId)
    {
        if (point == target)
        {
            Environment.FailFast($"Injected maintenance crash at {point}.");
        }
    }
}

file static class CrashScenario
{
    public const string BranchCommitPrefix = "BranchCommit-";
    public const string GarbageCollectionDuringCheckpointWrite = "GarbageCollectionDuringCheckpointWrite";
    public const string GarbageCollectionBeforeWalReset = "GarbageCollectionBeforeWalReset";
    public const string GarbageCollectionAfterWalReset = "GarbageCollectionAfterWalReset";
    public const string CompactionDuringOutputWrite = "CompactionDuringOutputWrite";
    public const string CompactionBeforePublish = "CompactionBeforePublish";
    public const string CompactionAfterPublish = "CompactionAfterPublish";
    public const string BranchCreateAfterIntent = "BranchCreateAfterIntent";
    public const string BranchCreateAfterBaseRoot = "BranchCreateAfterBaseRoot";
    public const string BranchCreateAfterActivation = "BranchCreateAfterActivation";
    public const string BranchDeleteAfterIntent = "BranchDeleteAfterIntent";
    public const string BranchDeleteAfterBaseRootDelete = "BranchDeleteAfterBaseRootDelete";
    public const string BranchSnapshotAfterFlush = "BranchSnapshotAfterFlush";
    public const string PhysicalPage = "AfterFirstPhysicalPage";
    public const string SnapshotBeforeWrite = "SnapshotBeforeRecordWrite";
    public const string SnapshotAfterWrite = "SnapshotAfterRecordWrite";
    public const string SnapshotBeforeFlush = "SnapshotBeforeFlush";
    public const string SnapshotAfterFlush = "SnapshotAfterFlush";
    public const string SnapshotAfterAcknowledgement = "SnapshotAfterAcknowledgement";
    public const string SnapshotDeleteBeforeWrite = "SnapshotDeleteBeforeRecordWrite";
    public const string SnapshotDeleteAfterWrite = "SnapshotDeleteAfterRecordWrite";
    public const string SnapshotDeleteBeforeFlush = "SnapshotDeleteBeforeFlush";
    public const string SnapshotDeleteAfterFlush = "SnapshotDeleteAfterFlush";
    public const string SnapshotThenLaterPhysicalCrash = "SnapshotThenLaterPhysicalCrash";
}
