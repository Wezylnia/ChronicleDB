using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChronicleDB.Core.Keys;
using ChronicleDB.Storage.Files;

var operations = args.Length >= 1 && int.TryParse(args[0], out var parsedOperations) ? parsedOperations : 250;
var workers = args.Length >= 2 && int.TryParse(args[1], out var parsedWorkers) ? parsedWorkers : 4;
var outputPath = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
if (operations <= 0 || workers <= 0)
{
    Console.Error.WriteLine(
        "Usage: ChronicleDB.Benchmarks [positive-operations] [positive-workers] [optional-json-output]");
    return 2;
}

var environment = new BenchmarkEnvironment(
    TimestampUtc: DateTimeOffset.UtcNow,
    OperatingSystem: RuntimeInformation.OSDescription,
    Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
    Framework: RuntimeInformation.FrameworkDescription,
    ProcessorCount: Environment.ProcessorCount,
    Operations: operations,
    Workers: workers);
var results = new List<BenchmarkResult>();

Console.WriteLine($"ChronicleDB v0.5 baseline runner operations={operations} workers={workers}");
Console.WriteLine(
    $"OS={environment.OperatingSystem} arch={environment.Architecture} runtime={environment.Framework} " +
    $"logical-cpus={environment.ProcessorCount}");

Run("B0 persistent-kv write", () => BenchmarkStorage(operations), results);
Run("B2 MVCC durable write", () => BenchmarkMvcc(operations), results);
Run("B3 concurrent MVCC", () => BenchmarkConcurrent(operations, workers), results);
Run("B4 current-state read", () => BenchmarkCurrentRead(operations), results);
Run("B4 historical read", () => BenchmarkHistorical(operations), results);
Run("snapshot create", () => BenchmarkSnapshotCreation(Math.Min(operations, 1_000)), results);
Run("recovery open", () => BenchmarkRecovery(operations), results);

if (outputPath is not null)
{
    var report = new BenchmarkReport(environment, results);
    var directory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"raw-json={outputPath}");
}

return 0;

static BenchmarkSample BenchmarkStorage(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var store = PersistentKeyValueStore.Open(directory);
        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            store.Put(new BinaryKey(BitConverter.GetBytes(index)), BitConverter.GetBytes(index));
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }

        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, new Dictionary<string, double>());
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkMvcc(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }

        var diagnostics = database.GetDiagnostics();
        return new BenchmarkSample(
            operations,
            Stopwatch.GetTimestamp() - overall,
            latencies,
            new Dictionary<string, double>
            {
                ["wal_bytes"] = diagnostics.WalBytesWrittenThisSession,
                ["wal_flushes"] = diagnostics.WalFlushCount,
                ["wal_flush_avg_ms"] = diagnostics.AverageWalFlushMilliseconds,
                ["commit_avg_ms"] = diagnostics.AverageCommitMilliseconds,
                ["versions"] = diagnostics.VersionCount
            });
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkConcurrent(int operations, int workers)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        var latencies = new long[operations];
        var next = -1;
        var overall = Stopwatch.GetTimestamp();
        Task.WaitAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= operations)
                {
                    return;
                }

                var started = Stopwatch.GetTimestamp();
                database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
                latencies[index] = Stopwatch.GetTimestamp() - started;
            }
        })).ToArray());

        var diagnostics = database.GetDiagnostics();
        return new BenchmarkSample(
            operations,
            Stopwatch.GetTimestamp() - overall,
            latencies,
            new Dictionary<string, double>
            {
                ["commit_serialization_contention"] = diagnostics.CommitSerializationContention,
                ["index_contention"] = diagnostics.IndexContention,
                ["wal_flush_avg_ms"] = diagnostics.AverageWalFlushMilliseconds
            });
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkCurrentRead(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < operations; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
        }

        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            if (!database.TryGet(BitConverter.GetBytes(index), out _))
            {
                throw new InvalidOperationException("Current-state benchmark lost a committed key.");
            }

            latencies[index] = Stopwatch.GetTimestamp() - started;
        }

        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, new Dictionary<string, double>());
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkHistorical(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < operations; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
        }

        using var snapshot = database.CreateSnapshot("benchmark-boundary");
        for (var index = 0; index < operations; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index + 1));
        }

        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            if (!snapshot.TryGet(BitConverter.GetBytes(index), out _))
            {
                throw new InvalidOperationException("Historical benchmark lost a retained key.");
            }

            latencies[index] = Stopwatch.GetTimestamp() - started;
        }

        var diagnostics = database.GetDiagnostics();
        return new BenchmarkSample(
            operations,
            Stopwatch.GetTimestamp() - overall,
            latencies,
            new Dictionary<string, double>
            {
                ["versions"] = diagnostics.VersionCount,
                ["average_chain_length"] = diagnostics.AverageVersionChainLength,
                ["maximum_chain_length"] = diagnostics.MaximumVersionChainLength,
                ["data_file_bytes"] = diagnostics.DataFileBytes,
                ["wal_file_bytes"] = diagnostics.WalFileBytes,
                ["snapshot_metadata_bytes"] = diagnostics.SnapshotMetadataBytes
            });
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkSnapshotCreation(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        database.Put([1], [1]);
        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            using var snapshot = database.CreateSnapshot($"bench-{index}");
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }

        var diagnostics = database.GetDiagnostics();
        return new BenchmarkSample(
            operations,
            Stopwatch.GetTimestamp() - overall,
            latencies,
            new Dictionary<string, double>
            {
                ["snapshot_metadata_bytes"] = diagnostics.SnapshotMetadataBytes,
                ["snapshot_count"] = diagnostics.SnapshotCount
            });
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkRecovery(int operations)
{
    var directory = NewDirectory();
    try
    {
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
        {
            for (var index = 0; index < operations; index++)
            {
                database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
            }

            using var snapshot = database.CreateSnapshot("recovery-benchmark");
        }

        var started = Stopwatch.GetTimestamp();
        using var recovered = ChronicleDB.ChronicleDatabase.Open(directory);
        var elapsed = Stopwatch.GetTimestamp() - started;
        if (recovered.Count != operations || recovered.ListSnapshots().Count != 1)
        {
            throw new InvalidOperationException("Recovery benchmark did not reconstruct expected state.");
        }

        var diagnostics = recovered.GetDiagnostics();
        return new BenchmarkSample(
            1,
            elapsed,
            [elapsed],
            new Dictionary<string, double>
            {
                ["replayed_transactions"] = diagnostics.RecoveryReplayedTransactions,
                ["wal_file_bytes"] = diagnostics.WalFileBytes,
                ["data_file_bytes"] = diagnostics.DataFileBytes
            });
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static void Run(string name, Func<BenchmarkSample> benchmark, ICollection<BenchmarkResult> results)
{
    _ = benchmark();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var gen0Before = GC.CollectionCount(0);
    var gen1Before = GC.CollectionCount(1);
    var gen2Before = GC.CollectionCount(2);
    var sample = benchmark();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    var result = Result(
        name,
        sample,
        allocated,
        GC.CollectionCount(0) - gen0Before,
        GC.CollectionCount(1) - gen1Before,
        GC.CollectionCount(2) - gen2Before);
    results.Add(result);
    Console.WriteLine(
        $"{name,-25} ops/s={result.OperationsPerSecond,10:F1} " +
        $"p50={result.P50Milliseconds,8:F3}ms p95={result.P95Milliseconds,8:F3}ms " +
        $"p99={result.P99Milliseconds,8:F3}ms alloc={result.AllocatedBytes,12}B");
}

static BenchmarkResult Result(
    string name,
    BenchmarkSample sample,
    long allocatedBytes,
    int gen0Collections,
    int gen1Collections,
    int gen2Collections)
{
    var latencies = (long[])sample.Latencies.Clone();
    Array.Sort(latencies);
    return new BenchmarkResult(
        name,
        sample.Operations * Stopwatch.Frequency / (double)sample.ElapsedTicks,
        ToMilliseconds(Percentile(latencies, 0.50)),
        ToMilliseconds(Percentile(latencies, 0.95)),
        ToMilliseconds(Percentile(latencies, 0.99)),
        allocatedBytes,
        gen0Collections,
        gen1Collections,
        gen2Collections,
        sample.Metrics);
}

static long Percentile(long[] sorted, double percentile)
{
    var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

static string NewDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), "chronicle-benchmark-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static void DeleteDirectory(string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

readonly record struct BenchmarkSample(
    int Operations,
    long ElapsedTicks,
    long[] Latencies,
    IReadOnlyDictionary<string, double> Metrics);

sealed record BenchmarkEnvironment(
    DateTimeOffset TimestampUtc,
    string OperatingSystem,
    string Architecture,
    string Framework,
    int ProcessorCount,
    int Operations,
    int Workers);

sealed record BenchmarkResult(
    string Name,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    IReadOnlyDictionary<string, double> Metrics);

sealed record BenchmarkReport(
    BenchmarkEnvironment Environment,
    IReadOnlyList<BenchmarkResult> Results);
