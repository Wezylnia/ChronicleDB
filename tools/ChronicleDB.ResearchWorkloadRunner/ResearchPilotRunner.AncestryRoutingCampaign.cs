using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static partial class ResearchPilotRunner
{
    private static int RunStableAncestryRoutingCampaign(string[] args)
    {
        if (args.Length < 7
            || !int.TryParse(args[0], out var seedStart)
            || !int.TryParse(args[1], out var seedCount)
            || !int.TryParse(args[2], out var repetitions)
            || !int.TryParse(args[3], out var depth)
            || !int.TryParse(args[4], out var keyCount)
            || !int.TryParse(args[5], out var reads)
            || seedCount is < 1 or > 20
            || repetitions is < 2 or > 10
            || depth is < 2 or > 16
            || keyCount is < 32 or > 4096
            || reads < 1_000
            || !TryParseAncestryDistribution(args[6], out var distribution))
        {
            Console.Error.WriteLine(
                "Usage: pilot P3BR <seed-start> <seed-count:1..20> <repetitions:2..10> " +
                "<depth:2..16> <key-count:32..4096> <reads>=1000 <uniform|zipf> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 8
            ? Path.GetFullPath(args[7])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p3br-{seedStart}-{seedCount}x{repetitions}-d{depth}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var trials = new List<(int Seed, int Repetition)>(checked(seedCount * repetitions));
            for (var seedOffset = 0; seedOffset < seedCount; seedOffset++)
            {
                var seed = checked(seedStart + seedOffset);
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    trials.Add((seed, repetition));
                }
            }

            var shuffle = new Random(StableCampaignShuffleSeed(
                seedStart,
                seedCount,
                repetitions,
                depth,
                keyCount,
                reads,
                (int)distribution));
            for (var index = trials.Count - 1; index > 0; index--)
            {
                var other = shuffle.Next(index + 1);
                (trials[index], trials[other]) = (trials[other], trials[index]);
            }

            File.WriteAllText(
                Path.Combine(outputDirectory, "p3br-plan.json"),
                JsonSerializer.Serialize(
                    trials.Select((trial, order) => new
                    {
                        trialOrder = order,
                        seed = trial.Seed,
                        repetition = trial.Repetition,
                    }),
                    JsonOptions));

            var runs = new List<StableAncestryRoutingCampaignRun>(trials.Count);
            for (var trialOrder = 0; trialOrder < trials.Count; trialOrder++)
            {
                var (seed, repetition) = trials[trialOrder];
                var runDirectory = Path.Combine(outputDirectory, $"trial-{trialOrder:D3}-seed-{seed}-rep-{repetition:D2}");
                Directory.CreateDirectory(runDirectory);
                var child = RunP3BChild(seed, depth, keyCount, reads, distribution, runDirectory);
                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"P3BR child failed for seed={seed} repetition={repetition}: {child.StandardError}");
                }

                var resultPath = Path.Combine(runDirectory, "p3b-result.json");
                if (!File.Exists(resultPath))
                {
                    throw new InvalidOperationException($"P3BR child did not produce '{resultPath}'.");
                }

                using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
                var root = document.RootElement;
                runs.Add(new StableAncestryRoutingCampaignRun(
                    Seed: seed,
                    Repetition: repetition,
                    TrialOrder: trialOrder,
                    WarmP99Speedup: root.GetProperty("warmP99Speedup").GetDouble(),
                    WarmMeanSpeedup: root.GetProperty("warmMeanSpeedup").GetDouble(),
                    RouteEntryCount: root.GetProperty("routeEntryCount").GetInt32(),
                    RouteHits: root.GetProperty("routeHits").GetInt64(),
                    RouteInvalidations: root.GetProperty("routeInvalidations").GetInt64(),
                    ReopenStartsEmpty: root.GetProperty("reopenStartsEmpty").GetBoolean(),
                    ReopenRebuilds: root.GetProperty("reopenRebuilds").GetBoolean(),
                    CompactionPreservedResults: root.GetProperty("compactionPreservedResults").GetBoolean(),
                    ChildStandardOutput: child.StandardOutput.Trim()));
            }

            var orderedP99 = runs.Select(run => run.WarmP99Speedup).Order().ToArray();
            var result = new StableAncestryRoutingCampaignResult(
                Pilot: "P3BR",
                SeedStart: seedStart,
                SeedCount: seedCount,
                Repetitions: repetitions,
                RunCount: runs.Count,
                Depth: depth,
                KeyCount: keyCount,
                Reads: reads,
                Distribution: distribution.ToString(),
                MedianWarmP99Speedup: Median(orderedP99),
                P05WarmP99Speedup: Percentile(orderedP99, 0.05),
                P95WarmP99Speedup: Percentile(orderedP99, 0.95),
                MinimumWarmP99Speedup: orderedP99[0],
                MaximumWarmP99Speedup: orderedP99[^1],
                MedianWarmMeanSpeedup: Median(runs.Select(run => run.WarmMeanSpeedup)),
                EveryRunCorrect: runs.All(run => run.RouteEntryCount > 0
                    && run.RouteHits > 0
                    && run.RouteInvalidations == 0
                    && run.ReopenStartsEmpty
                    && run.ReopenRebuilds
                    && run.CompactionPreservedResults),
                Runs: runs);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p3br-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));

            var pass = result.EveryRunCorrect && orderedP99.All(double.IsFinite);
            Console.WriteLine(
                $"P3BR {(pass ? "PASS" : "FAIL")} runs={runs.Count} depth={depth} dist={distribution} " +
                $"p99-speedup-median={result.MedianWarmP99Speedup:F2}x " +
                $"p05={result.P05WarmP99Speedup:F2}x p95={result.P95WarmP99Speedup:F2}x " +
                $"min={result.MinimumWarmP99Speedup:F2}x max={result.MaximumWarmP99Speedup:F2}x output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P3BR FAIL: {exception}");
            return 1;
        }
    }

    private static ChildProcessResult RunP3BChild(
        int seed,
        int depth,
        int keyCount,
        int reads,
        AncestryQueryDistribution distribution,
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
            "P3B",
            seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            keyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            distribution == AncestryQueryDistribution.Uniform ? "uniform" : "zipf",
            outputDirectory,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start P3B child process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("P3B child process exceeded the 180 second campaign limit.");
        }

        Task.WaitAll(standardOutputTask, standardErrorTask);
        return new ChildProcessResult(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
    }

    private sealed record StableAncestryRoutingCampaignRun(
        int Seed,
        int Repetition,
        int TrialOrder,
        double WarmP99Speedup,
        double WarmMeanSpeedup,
        int RouteEntryCount,
        long RouteHits,
        long RouteInvalidations,
        bool ReopenStartsEmpty,
        bool ReopenRebuilds,
        bool CompactionPreservedResults,
        string ChildStandardOutput);

    private sealed record StableAncestryRoutingCampaignResult(
        string Pilot,
        int SeedStart,
        int SeedCount,
        int Repetitions,
        int RunCount,
        int Depth,
        int KeyCount,
        int Reads,
        string Distribution,
        double MedianWarmP99Speedup,
        double P05WarmP99Speedup,
        double P95WarmP99Speedup,
        double MinimumWarmP99Speedup,
        double MaximumWarmP99Speedup,
        double MedianWarmMeanSpeedup,
        bool EveryRunCorrect,
        IReadOnlyList<StableAncestryRoutingCampaignRun> Runs);
}
