using ChronicleDB;
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
    var points = Enum.GetValues<TransactionFaultPoint>();
    var failures = 0;
    foreach (var point in points)
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
            startInfo.ArgumentList.Add(point.ToString());
            var process = System.Diagnostics.Process.Start(startInfo);
            process!.WaitForExit();

            using var database = ChronicleDatabase.Open(directory);
            var firstFound = database.TryGet([1], out var first);
            var secondFound = database.TryGet([2], out var second);
            var complete = firstFound
                && secondFound
                && first.SequenceEqual(new byte[] { 11 })
                && second.SequenceEqual(new byte[] { 22 });
            var validOutcome = point switch
            {
                TransactionFaultPoint.BeforeWalAppend => !complete,
                TransactionFaultPoint.AfterWalAppend or TransactionFaultPoint.BeforeWalFlush => true,
                _ => complete
            };
            if (!validOutcome)
            {
                Console.Error.WriteLine($"{point}: recovery result was not valid, complete={complete}");
                failures++;
            }
            else
            {
                Console.WriteLine($"{point}: {(complete ? "committed" : "not-visible")}");
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
    if (!Enum.TryParse<TransactionFaultPoint>(pointName, ignoreCase: true, out var point))
    {
        return 2;
    }

    using var database = ChronicleDatabase.Open(directory, faultInjector: new CrashInjector(point));
    using var transaction = database.BeginTransaction();
    transaction.Put([1], [11]);
    transaction.Put([2], [22]);
    transaction.Commit();
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
