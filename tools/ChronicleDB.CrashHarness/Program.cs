using ChronicleDB;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Transactions.Faults;

if (args.Length == 0 || args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    return await RunHarnessAsync();
}

if (args.Length == 3 && args[0].Equals("child", StringComparison.OrdinalIgnoreCase))
{
    return RunChild(args[1], args[2]);
}

Console.Error.WriteLine("Usage: run | child <directory> <fault-point>");
return 2;

static async Task<int> RunHarnessAsync()
{
    var scenarios = Enum.GetValues<TransactionFaultPoint>()
        .Select(point => point.ToString())
        .Append(CrashScenario.PhysicalPage)
        .ToArray();
    var failures = 0;
    foreach (var scenario in scenarios)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
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
            var process = System.Diagnostics.Process.Start(startInfo);
            process!.WaitForExit();

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
            var validOutcome = atomic && durableExpectation;
            if (!validOutcome)
            {
                Console.Error.WriteLine(
                    $"{scenario}: recovery result was not valid, atomic={atomic}, complete={complete}");
                failures++;
            }
            else
            {
                Console.WriteLine($"{scenario}: {(complete ? "committed" : "not-visible")}");
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

    await Task.CompletedTask;
    return failures == 0 ? 0 : 1;
}

static int RunChild(string directory, string pointName)
{
    if (pointName.Equals(CrashScenario.PhysicalPage, StringComparison.OrdinalIgnoreCase))
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

    if (!Enum.TryParse<TransactionFaultPoint>(pointName, ignoreCase: true, out var point))
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

file static class CrashScenario
{
    public const string PhysicalPage = "AfterFirstPhysicalPage";
}
