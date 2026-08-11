using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChronicleDB.Core.Keys;
using ChronicleDB.Maintenance;
using ChronicleDB.Storage.Files;

var operations = args.Length >= 1 && int.TryParse(args[0], out var parsedOperations) ? parsedOperations : 250;
var workers = args.Length >= 2 && int.TryParse(args[1], out var parsedWorkers) ? parsedWorkers : 4;
var outputPath = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
var seed = args.Length >= 4 && int.TryParse(args[3], out var parsedSeed) ? parsedSeed : 42;
if (operations <= 0 || workers <= 0)
{
    Console.Error.WriteLine(
        "Usage: ChronicleDB.Benchmarks [positive-operations] [positive-workers] [optional-json-output] [optional-seed]");
    return 2;
}

var environment = new BenchmarkEnvironment(
    ChronicleDbRelease: "v1.0",
    CommitHash: ResolveCommitHash(),
    BuildConfiguration: BuildConfigurationName(),
    TimestampUtc: DateTimeOffset.UtcNow,
    OperatingSystem: RuntimeInformation.OSDescription,
    Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
    Framework: RuntimeInformation.FrameworkDescription,
    DotNetSdk: ResolveDotNetSdkVersion(),
    ProcessorCount: Environment.ProcessorCount,
    Operations: operations,
    Workers: workers,
    Seed: seed,
    PageSize: 16 * 1024,
    KeyBytes: sizeof(int),
    ValueBytes: sizeof(int),
    DurabilityMode: "WAL flush-to-disk per acknowledged commit");
var results = new List<BenchmarkResult>();

Console.WriteLine($"ChronicleDB v1.0 research runner operations={operations} workers={workers} seed={seed}");
Console.WriteLine(
    $"OS={environment.OperatingSystem} arch={environment.Architecture} runtime={environment.Framework} " +
    $"logical-cpus={environment.ProcessorCount}");

Run("storage primitive write", () => BenchmarkStorage(operations), results);
Run("B2 main durable write", () => BenchmarkMvcc(operations), results);
Run("B2 main concurrent write", () => BenchmarkConcurrent(operations, workers), results);
Run("current-state read", () => BenchmarkCurrentRead(operations), results);
Run("historical read", () => BenchmarkHistorical(operations), results);
Run("snapshot create", () => BenchmarkSnapshotCreation(Math.Min(operations, 250)), results);
Run("branch create", () => BenchmarkBranchCreation(Math.Min(operations, 100)), results);
Run("branch inherited read", () => BenchmarkBranchInheritedRead(operations), results);
Run("branch local write", () => BenchmarkBranchLocalWrite(operations), results);
Run("branch storage amplification", () => BenchmarkBranchStorageAmplification(Math.Max(operations, 64), 10), results);
Run("snapshot retention amplification", () => BenchmarkSnapshotRetentionAmplification(Math.Max(operations, 64)), results);
Run("B3 branch-scale-1", () => BenchmarkBranchScale(operations, 1, seed), results);
Run("B3 branch-scale-10", () => BenchmarkBranchScale(operations, 10, seed), results);
Run("B4 branch-scale-25", () => BenchmarkBranchScale(operations, 25, seed), results);
Run("B4 branch-scale-50", () => BenchmarkBranchScale(operations, 50, seed), results);
Run("B4 branch-scale-100", () => BenchmarkBranchScale(operations, 100, seed), results);
Run("B6 gc pass", () => BenchmarkGarbageCollection(Math.Max(operations, 64)), results);
Run("B8 compaction pass", () => BenchmarkCompaction(Math.Max(operations, 64)), results);
Run("recovery open", () => BenchmarkRecovery(operations, Math.Min(10, Math.Max(1, workers))), results);

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

        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("data_file_bytes", store.DataLength),
            ("data_pages", store.PageCount)));
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
        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("wal_bytes", diagnostics.WalBytesWrittenThisSession),
            ("wal_flushes", diagnostics.WalFlushCount),
            ("wal_flush_avg_ms", diagnostics.AverageWalFlushMilliseconds),
            ("commit_avg_ms", diagnostics.AverageCommitMilliseconds),
            ("versions", diagnostics.VersionCount)));
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
        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("commit_serialization_contention", diagnostics.CommitSerializationContention),
            ("index_contention", diagnostics.IndexContention),
            ("wal_flush_avg_ms", diagnostics.AverageWalFlushMilliseconds)));
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

        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics());
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
        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("versions", diagnostics.VersionCount),
            ("average_chain_length", diagnostics.AverageVersionChainLength),
            ("maximum_chain_length", diagnostics.MaximumVersionChainLength),
            ("data_file_bytes", diagnostics.DataFileBytes),
            ("wal_file_bytes", diagnostics.WalFileBytes),
            ("snapshot_metadata_bytes", diagnostics.SnapshotMetadataBytes)));
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
        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("snapshot_metadata_bytes", diagnostics.SnapshotMetadataBytes),
            ("root_metadata_bytes", diagnostics.HistoryRootMetadataBytes),
            ("snapshot_count", diagnostics.SnapshotCount)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkBranchCreation(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < 64; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
        }

        var before = database.GetDiagnostics();
        var latencies = new long[operations];
        var handles = new List<ChronicleDB.ChronicleBranch>(operations);
        try
        {
            var overall = Stopwatch.GetTimestamp();
            for (var index = 0; index < operations; index++)
            {
                var started = Stopwatch.GetTimestamp();
                handles.Add(database.CreateBranch($"bench-branch-{index}"));
                latencies[index] = Stopwatch.GetTimestamp() - started;
            }
            var elapsed = Stopwatch.GetTimestamp() - overall;
            var after = database.GetDiagnostics();
            return new BenchmarkSample(operations, elapsed, latencies, Metrics(
                ("branch_count", after.BranchCount),
                ("branch_metadata_delta_bytes", after.BranchMetadataBytes - before.BranchMetadataBytes),
                ("branch_private_data_bytes", after.BranchLocalDataBytes),
                ("branch_wal_bytes", after.BranchLocalWalBytes),
                ("main_data_bytes", after.DataFileBytes)));
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkBranchInheritedRead(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < operations; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
        }
        using var branch = database.CreateBranch("inherited-read");
        for (var index = 0; index < operations; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index + 1));
        }

        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            if (!branch.TryGet(BitConverter.GetBytes(index), out var value)
                || BitConverter.ToInt32(value) != index)
            {
                throw new InvalidOperationException("Inherited branch read did not resolve the fixed parent base.");
            }
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }
        var topology = database.GetHistoryTopologyDiagnostics();
        var history = topology.Branches.Single(item => item.Name == "inherited-read");
        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("branch_versions", history.VersionCount),
            ("branch_data_bytes", history.DataFileBytes),
            ("parent_base_sequence", history.ParentBaseSequence ?? 0)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkBranchLocalWrite(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        using var branch = database.CreateBranch("local-write");
        var latencies = new long[operations];
        var overall = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            branch.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }
        var history = database.GetHistoryTopologyDiagnostics().Branches.Single(item => item.Name == "local-write");
        return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
            ("branch_versions", history.VersionCount),
            ("branch_data_bytes", history.DataFileBytes),
            ("branch_wal_bytes", history.WalFileBytes)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkBranchStorageAmplification(int operations, int branchCount)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        var keyCount = Math.Clamp(operations, 64, 256);
        for (var index = 0; index < keyCount; index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
        }

        var branches = Enumerable.Range(0, branchCount)
            .Select(index => database.CreateBranch($"amplification-{index}"))
            .ToArray();
        try
        {
            var before = database.GetDiagnostics();
            var writesPerBranch = Math.Max(1, keyCount / 4);
            var totalWrites = checked(writesPerBranch * branchCount);
            var latencies = new long[totalWrites];
            var sampleIndex = 0;
            var overall = Stopwatch.GetTimestamp();
            for (var branchIndex = 0; branchIndex < branches.Length; branchIndex++)
            {
                for (var write = 0; write < writesPerBranch; write++)
                {
                    var keyIndex = (branchIndex * writesPerBranch + write) % keyCount;
                    var started = Stopwatch.GetTimestamp();
                    branches[branchIndex].Put(
                        BitConverter.GetBytes(keyIndex),
                        BitConverter.GetBytes(branchIndex + keyIndex + 1));
                    latencies[sampleIndex++] = Stopwatch.GetTimestamp() - started;
                }
            }

            var after = database.GetDiagnostics();
            var privateDelta = after.BranchLocalDataBytes - before.BranchLocalDataBytes;
            return new BenchmarkSample(totalWrites, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
                ("branch_count", branchCount),
                ("main_data_bytes", after.DataFileBytes),
                ("branch_private_data_bytes_before", before.BranchLocalDataBytes),
                ("branch_private_data_bytes_after", after.BranchLocalDataBytes),
                ("branch_private_data_delta_bytes", privateDelta),
                ("branch_wal_bytes", after.BranchLocalWalBytes),
                ("logical_branch_writes", totalWrites),
                ("private_bytes_per_logical_write", totalWrites == 0 ? 0 : privateDelta / (double)totalWrites)));
        }
        finally
        {
            foreach (var branch in branches)
            {
                branch.Dispose();
            }
        }
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkSnapshotRetentionAmplification(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        var firstPhase = Math.Max(1, operations / 4);
        for (var index = 0; index < firstPhase; index++)
        {
            database.Put([1], BitConverter.GetBytes(index));
        }
        using var oldSnapshot = database.CreateSnapshot("retention-old");
        for (var index = firstPhase; index < operations; index++)
        {
            database.Put([1], BitConverter.GetBytes(index));
        }

        var before = database.GetDiagnostics();
        var started = Stopwatch.GetTimestamp();
        var gc = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 2,
            IncludeBranches = true,
        });
        var elapsed = Stopwatch.GetTimestamp() - started;
        if (!oldSnapshot.TryGet([1], out var historical)
            || BitConverter.ToInt32(historical) != firstPhase - 1)
        {
            throw new InvalidOperationException("GC changed an old retained snapshot during the retention benchmark.");
        }

        var after = database.GetDiagnostics();
        return new BenchmarkSample(1, elapsed, [elapsed], Metrics(
            ("snapshot_age_commits", operations - firstPhase),
            ("versions_before_gc", before.VersionCount),
            ("versions_after_gc", after.VersionCount),
            ("versions_reclaimed", gc.VersionsReclaimed),
            ("retaining_roots", after.RetainingRootCount),
            ("history_checkpoint_bytes", after.HistoryCheckpointBytes),
            ("data_file_bytes", after.DataFileBytes)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkBranchScale(int operations, int branchCount, int seed)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < Math.Min(operations, 128); index++)
        {
            database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
        }

        var branches = Enumerable.Range(0, branchCount)
            .Select(index => database.CreateBranch($"scale-{index}"))
            .ToArray();
        try
        {
            var random = new Random(seed);
            var latencies = new long[operations];
            var overall = Stopwatch.GetTimestamp();
            for (var index = 0; index < operations; index++)
            {
                var branch = branches[random.Next(branches.Length)];
                var key = BitConverter.GetBytes(index % Math.Min(operations, 128));
                var started = Stopwatch.GetTimestamp();
                _ = branch.TryGet(key, out _);
                latencies[index] = Stopwatch.GetTimestamp() - started;
            }
            var diagnostics = database.GetDiagnostics();
            return new BenchmarkSample(operations, Stopwatch.GetTimestamp() - overall, latencies, Metrics(
                ("branch_count", diagnostics.BranchCount),
                ("branch_metadata_bytes", diagnostics.BranchMetadataBytes),
                ("branch_private_data_bytes", diagnostics.BranchLocalDataBytes),
                ("branch_wal_bytes", diagnostics.BranchLocalWalBytes),
                ("retaining_roots", diagnostics.RetainingRootCount)));
        }
        finally
        {
            foreach (var branch in branches)
            {
                branch.Dispose();
            }
        }
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkGarbageCollection(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < operations; index++)
        {
            database.Put([1], BitConverter.GetBytes(index));
        }
        using (database.CreateSnapshot("gc-retained"))
        {
        }
        for (var index = 0; index < operations; index++)
        {
            database.Put([2], BitConverter.GetBytes(index));
        }

        var before = database.GetDiagnostics();
        var started = Stopwatch.GetTimestamp();
        var result = database.RunGarbageCollection(new GarbageCollectionOptions
        {
            RetainRecentCommits = 8,
            IncludeBranches = true,
        });
        var elapsed = Stopwatch.GetTimestamp() - started;
        var after = database.GetDiagnostics();
        return new BenchmarkSample(1, elapsed, [elapsed], Metrics(
            ("histories_processed", result.HistoriesProcessed),
            ("versions_reclaimed", result.VersionsReclaimed),
            ("checkpoint_bytes_written", result.CheckpointBytesWritten),
            ("versions_before", before.VersionCount),
            ("versions_after", after.VersionCount),
            ("wal_bytes_after", after.WalFileBytes),
            ("retention_floor", result.MainRetentionFloor)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkCompaction(int operations)
{
    var directory = NewDirectory();
    try
    {
        using var database = ChronicleDB.ChronicleDatabase.Open(directory);
        for (var index = 0; index < operations; index++)
        {
            database.Put([1], BitConverter.GetBytes(index));
            database.Put([2], BitConverter.GetBytes(index));
        }
        _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 4 });
        var before = database.GetDiagnostics();
        var started = Stopwatch.GetTimestamp();
        var result = database.RunCompaction(new CompactionOptions
        {
            MaxHistoriesPerPass = 4,
            MinimumReclaimableBytes = 1,
            MaxBytesRewrittenPerPass = long.MaxValue,
        });
        var elapsed = Stopwatch.GetTimestamp() - started;
        var after = database.GetDiagnostics();
        return new BenchmarkSample(1, elapsed, [elapsed], Metrics(
            ("histories_compacted", result.HistoriesCompacted),
            ("bytes_rewritten", result.BytesRewritten),
            ("bytes_reclaimed", result.BytesReclaimed),
            ("data_bytes_before", before.DataFileBytes),
            ("data_bytes_after", after.DataFileBytes),
            ("checkpoint_bytes", after.HistoryCheckpointBytes)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static BenchmarkSample BenchmarkRecovery(int operations, int branchCount)
{
    var directory = NewDirectory();
    try
    {
        var branchIds = new List<Guid>();
        using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
        {
            for (var index = 0; index < operations; index++)
            {
                database.Put(BitConverter.GetBytes(index), BitConverter.GetBytes(index));
            }
            using (database.CreateSnapshot("recovery-main"))
            {
            }
            for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                using var branch = database.CreateBranch($"recovery-{branchIndex}");
                branchIds.Add(branch.BranchId);
                for (var write = 0; write < Math.Min(operations, 32); write++)
                {
                    branch.Put(BitConverter.GetBytes(write), BitConverter.GetBytes(branchIndex + write));
                }
                using var branchSnapshot = branch.CreateSnapshot("stable");
            }
        }

        var started = Stopwatch.GetTimestamp();
        using var recovered = ChronicleDB.ChronicleDatabase.Open(directory);
        var elapsed = Stopwatch.GetTimestamp() - started;
        if (recovered.Count != operations || recovered.ListSnapshots().Count != 1 || recovered.ListBranches().Count != branchCount)
        {
            throw new InvalidOperationException("Recovery benchmark did not reconstruct expected topology.");
        }
        foreach (var branchId in branchIds)
        {
            using var branch = recovered.OpenBranch(branchId);
            if (branch.ListSnapshots().Count != 1)
            {
                throw new InvalidOperationException("Recovery benchmark lost a branch snapshot.");
            }
        }

        var diagnostics = recovered.GetDiagnostics();
        return new BenchmarkSample(1, elapsed, [elapsed], Metrics(
            ("replayed_transactions", diagnostics.RecoveryReplayedTransactions),
            ("main_wal_bytes", diagnostics.WalFileBytes),
            ("branch_wal_bytes", diagnostics.BranchLocalWalBytes),
            ("data_file_bytes", diagnostics.DataFileBytes),
            ("branch_count", diagnostics.BranchCount),
            ("snapshot_count", diagnostics.SnapshotCount + diagnostics.BranchSnapshotCount)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
}

static string BuildConfigurationName()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}

static string ResolveDotNetSdkVersion()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is not null && process.WaitForExit(2_000) && process.ExitCode == 0)
        {
            var value = process.StandardOutput.ReadToEnd().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
    }
    catch (InvalidOperationException)
    {
    }
    catch (System.ComponentModel.Win32Exception)
    {
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }

    return "unknown";
}

static string ResolveCommitHash()
{
    var explicitHash = Environment.GetEnvironmentVariable("CHRONICLEDB_COMMIT");
    if (!string.IsNullOrWhiteSpace(explicitHash))
    {
        return explicitHash.Trim();
    }

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is not null && process.WaitForExit(2_000) && process.ExitCode == 0)
        {
            var value = process.StandardOutput.ReadToEnd().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
    }
    catch (InvalidOperationException)
    {
    }
    catch (System.ComponentModel.Win32Exception)
    {
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }

    return "unknown";
}

static Dictionary<string, double> Metrics(params (string Name, double Value)[] values)
    => values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);

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
        $"{name,-26} ops/s={result.OperationsPerSecond,10:F1} " +
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
    string ChronicleDbRelease,
    string CommitHash,
    string BuildConfiguration,
    DateTimeOffset TimestampUtc,
    string OperatingSystem,
    string Architecture,
    string Framework,
    string DotNetSdk,
    int ProcessorCount,
    int Operations,
    int Workers,
    int Seed,
    int PageSize,
    int KeyBytes,
    int ValueBytes,
    string DurabilityMode);

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
