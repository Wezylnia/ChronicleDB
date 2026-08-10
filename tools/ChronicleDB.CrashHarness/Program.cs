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
            CrashScenario.SnapshotThenLaterPhysicalCrash
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

file static class CrashScenario
{
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
