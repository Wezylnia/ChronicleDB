using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static partial class ResearchPilotRunner
{
    private static int RunRetentionInterferenceCampaign(string[] args)
    {
        if (args.Length < 9
            || !int.TryParse(args[0], out var seedStart)
            || !int.TryParse(args[1], out var seedCount)
            || !int.TryParse(args[2], out var repetitions)
            || !int.TryParse(args[3], out var baseKeyCount)
            || !int.TryParse(args[4], out var valueBytes)
            || !int.TryParse(args[5], out var churnRounds)
            || !int.TryParse(args[6], out var hotKeyCount)
            || !int.TryParse(args[7], out var privateBytes)
            || !int.TryParse(args[8], out var readBudget)
            || seedCount is < 1 or > 20
            || repetitions is < 2 or > 10
            || baseKeyCount < 16
            || valueBytes <= 0
            || churnRounds <= 0
            || hotKeyCount <= 0
            || hotKeyCount > baseKeyCount
            || privateBytes <= 0
            || readBudget < 1_000)
        {
            Console.Error.WriteLine(
                "Usage: pilot P1IR <seed-start> <seed-count:1..20> <repetitions:2..10> " +
                "<base-key-count>=16 <value-bytes> <churn-rounds> <hot-key-count> " +
                "<private-bytes> <read-budget>=1000 [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 10
            ? Path.GetFullPath(args[9])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p1ir-{seedStart}-{seedCount}x{repetitions}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var runs = new List<RetentionInterferenceCampaignRun>(checked(seedCount * repetitions));
            var trials = new List<(int Seed, int Repetition)>(checked(seedCount * repetitions));
            for (var seedOffset = 0; seedOffset < seedCount; seedOffset++)
            {
                var seed = checked(seedStart + seedOffset);
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    trials.Add((seed, repetition));
                }
            }

            var shuffleHash = new HashCode();
            shuffleHash.Add(seedStart);
            shuffleHash.Add(seedCount);
            shuffleHash.Add(repetitions);
            shuffleHash.Add(baseKeyCount);
            shuffleHash.Add(valueBytes);
            shuffleHash.Add(churnRounds);
            shuffleHash.Add(hotKeyCount);
            shuffleHash.Add(privateBytes);
            shuffleHash.Add(readBudget);
            var shuffle = new Random(shuffleHash.ToHashCode());
            for (var index = trials.Count - 1; index > 0; index--)
            {
                var other = shuffle.Next(index + 1);
                (trials[index], trials[other]) = (trials[other], trials[index]);
            }

            File.WriteAllText(
                Path.Combine(outputDirectory, "p1ir-plan.json"),
                JsonSerializer.Serialize(
                    trials.Select((trial, order) => new
                    {
                        trialOrder = order,
                        seed = trial.Seed,
                        repetition = trial.Repetition,
                    }),
                    JsonOptions));

            for (var trialOrder = 0; trialOrder < trials.Count; trialOrder++)
            {
                var (seed, repetition) = trials[trialOrder];
                var runDirectory = Path.Combine(outputDirectory, $"trial-{trialOrder:D3}-seed-{seed}-rep-{repetition:D2}");
                Directory.CreateDirectory(runDirectory);
                var processResult = RunP1IChild(
                    seed,
                    baseKeyCount,
                    valueBytes,
                    churnRounds,
                    hotKeyCount,
                    privateBytes,
                    readBudget,
                    runDirectory);
                if (processResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"P1IR child failed for seed={seed} repetition={repetition}: {processResult.StandardError}");
                }

                var resultPath = Path.Combine(runDirectory, "p1i-result.json");
                if (!File.Exists(resultPath))
                {
                    throw new InvalidOperationException($"P1IR child did not produce '{resultPath}'.");
                }

                using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
                var root = document.RootElement;
                runs.Add(new RetentionInterferenceCampaignRun(
                    Seed: seed,
                    Repetition: repetition,
                    TrialOrder: trialOrder,
                    P99InterferenceRatio: root.GetProperty("p99InterferenceRatio").GetDouble(),
                    P95InterferenceRatio: root.GetProperty("p95InterferenceRatio").GetDouble(),
                    MaximumPauseRatio: root.GetProperty("maximumPauseRatio").GetDouble(),
                    MaintenanceElapsedMilliseconds: root.GetProperty("maintenanceElapsedMilliseconds").GetDouble(),
                    ExactMarginalPayloadBytes: root.GetProperty("exactMarginalPayloadBytes").GetInt64(),
                    CoarseRootInducedPayloadBytes: root.GetProperty("coarseRootInducedPayloadBytes").GetInt64(),
                    CompactionBytesRewritten: root.GetProperty("compactionBytesRewritten").GetInt64(),
                    CompactionBytesReclaimed: root.GetProperty("compactionBytesReclaimed").GetInt64(),
                    AllocatedBytesReleased: root.GetProperty("allocatedBytesReleased").GetInt64(),
                    AllocationMeasurementExact: root.GetProperty("allocationMeasurementExact").GetBoolean(),
                    ChildStandardOutput: processResult.StandardOutput.Trim()));
            }

            var result = new RetentionInterferenceCampaignResult(
                Pilot: "P1IR",
                SeedStart: seedStart,
                SeedCount: seedCount,
                Repetitions: repetitions,
                RunCount: runs.Count,
                BaseKeyCount: baseKeyCount,
                ValueBytes: valueBytes,
                ChurnRounds: churnRounds,
                HotKeyCount: hotKeyCount,
                PrivateBytes: privateBytes,
                ReadBudget: readBudget,
                MedianP99InterferenceRatio: Median(runs.Select(run => run.P99InterferenceRatio)),
                P95OfP99InterferenceRatio: Percentile(runs.Select(run => run.P99InterferenceRatio).Order().ToArray(), 0.95),
                MaximumP99InterferenceRatio: runs.Max(run => run.P99InterferenceRatio),
                MedianP95InterferenceRatio: Median(runs.Select(run => run.P95InterferenceRatio)),
                MedianMaintenanceMilliseconds: Median(runs.Select(run => run.MaintenanceElapsedMilliseconds)),
                MedianReclamationEfficiency: Median(runs.Select(run => Ratio(run.CompactionBytesReclaimed, run.CompactionBytesRewritten))),
                EveryAllocationMeasurementExact: runs.All(run => run.AllocationMeasurementExact),
                Runs: runs);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p1ir-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));

            var pass = result.EveryAllocationMeasurementExact
                && runs.All(run => run.ExactMarginalPayloadBytes > 0)
                && runs.All(run => run.CompactionBytesReclaimed >= 0)
                && runs.All(run => double.IsFinite(run.P99InterferenceRatio));
            Console.WriteLine(
                $"P1IR {(pass ? "PASS" : "FAIL")} runs={runs.Count} " +
                $"p99-median={result.MedianP99InterferenceRatio:F2}x " +
                $"p99-p95={result.P95OfP99InterferenceRatio:F2}x " +
                $"p99-max={result.MaximumP99InterferenceRatio:F2}x " +
                $"maintenance-median={result.MedianMaintenanceMilliseconds:F2}ms output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1IR FAIL: {exception}");
            return 1;
        }
    }

    private static ChildProcessResult RunP1IChild(
        int seed,
        int baseKeyCount,
        int valueBytes,
        int churnRounds,
        int hotKeyCount,
        int privateBytes,
        int readBudget,
        string outputDirectory)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the current process executable.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Cannot resolve the research runner entry assembly.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(entryAssembly);
        }

        foreach (var argument in new[]
        {
            "pilot",
            "P1I",
            seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            baseKeyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            valueBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            churnRounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            hotKeyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            privateBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            readBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
            outputDirectory,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start P1I child process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("P1I child process exceeded the 180 second campaign limit.");
        }

        Task.WaitAll(standardOutputTask, standardErrorTask);
        return new ChildProcessResult(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0d;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
    }

    private sealed record ChildProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record RetentionInterferenceCampaignRun(
        int Seed,
        int Repetition,
        int TrialOrder,
        double P99InterferenceRatio,
        double P95InterferenceRatio,
        double MaximumPauseRatio,
        double MaintenanceElapsedMilliseconds,
        long ExactMarginalPayloadBytes,
        long CoarseRootInducedPayloadBytes,
        long CompactionBytesRewritten,
        long CompactionBytesReclaimed,
        long AllocatedBytesReleased,
        bool AllocationMeasurementExact,
        string ChildStandardOutput);

    private sealed record RetentionInterferenceCampaignResult(
        string Pilot,
        int SeedStart,
        int SeedCount,
        int Repetitions,
        int RunCount,
        int BaseKeyCount,
        int ValueBytes,
        int ChurnRounds,
        int HotKeyCount,
        int PrivateBytes,
        int ReadBudget,
        double MedianP99InterferenceRatio,
        double P95OfP99InterferenceRatio,
        double MaximumP99InterferenceRatio,
        double MedianP95InterferenceRatio,
        double MedianMaintenanceMilliseconds,
        double MedianReclamationEfficiency,
        bool EveryAllocationMeasurementExact,
        IReadOnlyList<RetentionInterferenceCampaignRun> Runs);
}
