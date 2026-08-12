using System.Diagnostics;
using System.Text.Json;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;

internal static partial class ResearchPilotRunner
{
    private static int RunRecoveryMatrixPilot(string[] args)
    {
        if (args.Length < 7
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var historyCount)
            || !int.TryParse(args[2], out var checkpointCommits)
            || !int.TryParse(args[3], out var walTailCommits)
            || !int.TryParse(args[4], out var valueBytes)
            || !int.TryParse(args[5], out var requestedIndex)
            || !int.TryParse(args[6], out var repetitions)
            || historyCount is < 4 or > 32
            || checkpointCommits is < 1 or > 500
            || walTailCommits is < 0 or > 500
            || valueBytes is < 16 or > 65_536
            || requestedIndex < 0
            || requestedIndex >= historyCount
            || repetitions is < 2 or > 30)
        {
            Console.Error.WriteLine(
                "Usage: pilot P4M <seed> <history-count:4..32> <checkpoint-commits:1..500> " +
                "<wal-tail-commits:0..500> <value-bytes:16..65536> <requested-index> " +
                "<repetitions:2..30> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 8
            ? Path.GetFullPath(args[7])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p4m-{seed}-{historyCount}-{checkpointCommits}-{walTailCommits}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var sourceDirectory = Path.Combine(outputDirectory, "source");

        try
        {
            var random = new Random(seed);
            var historyIds = new List<Guid>(historyCount);
            GarbageCollectionResult checkpointResult;
            using (var database = ChronicleDatabase.Open(sourceDirectory))
            {
                var branches = new List<ChronicleBranch>(historyCount);
                try
                {
                    for (var index = 0; index < historyCount; index++)
                    {
                        var branch = database.CreateBranch($"p4m-{index:D2}");
                        branches.Add(branch);
                        historyIds.Add(branch.HistoryId);
                        for (var commit = 0; commit < checkpointCommits; commit++)
                        {
                            branch.Put(
                                Key(checked((index * 1_000_000) + commit)),
                                Payload(valueBytes, random, salt: checked((index * 100_000) + commit)));
                        }
                    }

                    checkpointResult = database.RunGarbageCollection(
                        new GarbageCollectionOptions { RetainRecentCommits = 0, IncludeBranches = true });

                    for (var index = 0; index < branches.Count; index++)
                    {
                        for (var commit = 0; commit < walTailCommits; commit++)
                        {
                            branches[index].Put(
                                Key(checked((index * 1_000_000) + checkpointCommits + commit)),
                                Payload(
                                    valueBytes,
                                    random,
                                    salt: checked(50_000_000 + (index * 100_000) + commit)));
                        }
                    }
                }
                finally
                {
                    foreach (var branch in branches)
                    {
                        branch.Dispose();
                    }
                }
            }

            var sourceWalBytes = Directory.EnumerateFiles(
                    sourceDirectory,
                    "*.wal",
                    SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            var sourceCheckpointBytes = Directory.EnumerateFiles(
                    sourceDirectory,
                    "chronicle.history",
                    SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);

            var runs = new List<RecoveryMatrixRun>(checked(repetitions * 2));
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                var runDirectory = Path.Combine(outputDirectory, $"run-{repetition:D2}");
                CopyDirectory(sourceDirectory, runDirectory);

                runs.Add(MeasureRecoveryMatrixOpen(
                    runDirectory,
                    historyIds,
                    requestedIndex,
                    repetition,
                    RecoveryCacheState.FreshImageOpen));
                runs.Add(MeasureRecoveryMatrixOpen(
                    runDirectory,
                    historyIds,
                    requestedIndex,
                    repetition,
                    RecoveryCacheState.ImmediateWarmReopen));
            }

            var freshRuns = runs.Where(run => run.CacheState == RecoveryCacheState.FreshImageOpen).ToArray();
            var warmRuns = runs.Where(run => run.CacheState == RecoveryCacheState.ImmediateWarmReopen).ToArray();
            var result = new RecoveryMatrixPilotResult(
                Pilot: "P4M",
                Seed: seed,
                HistoryCount: historyCount,
                CheckpointCommitsPerHistory: checkpointCommits,
                WalTailCommitsPerHistory: walTailCommits,
                ValueBytes: valueBytes,
                RequestedIndex: requestedIndex,
                Repetitions: repetitions,
                SourceCheckpointBytesWrittenByGc: checkpointResult.CheckpointBytesWritten,
                SourceCheckpointFileBytes: sourceCheckpointBytes,
                SourceWalFileBytes: sourceWalBytes,
                FreshImage: SummarizeRecoveryRuns(freshRuns),
                WarmReopen: SummarizeRecoveryRuns(warmRuns),
                Runs: runs,
                CurrentSemanticsAllowsSelectiveHistoryReady: false,
                FreshImageIsOsColdCacheClaim: false);

            File.WriteAllText(
                Path.Combine(outputDirectory, "p4m-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));

            var pass = runs.All(run => run.TelemetryComplete)
                && runs.All(run => run.RecoveryCompletedMilliseconds > 0d)
                && runs.All(run => run.BranchRuntimesOpenMilliseconds >= run.RequestedBranchRecoveryMilliseconds)
                && sourceCheckpointBytes > 0
                && (walTailCommits == 0 || sourceWalBytes > 0);
            Console.WriteLine(
                $"P4M {(pass ? "PASS" : "FAIL")} histories={historyCount} checkpoint-commits={checkpointCommits} " +
                $"wal-tail={walTailCommits} reps={repetitions} " +
                $"fresh-p50={result.FreshImage.RecoveryP50Milliseconds:F2}ms " +
                $"fresh-p95={result.FreshImage.RecoveryP95Milliseconds:F2}ms " +
                $"warm-p50={result.WarmReopen.RecoveryP50Milliseconds:F2}ms " +
                $"catalog-p50={result.FreshImage.CatalogP50Milliseconds:F2}ms " +
                $"branches-p50={result.FreshImage.BranchRuntimesP50Milliseconds:F2}ms " +
                $"requested-p50={result.FreshImage.RequestedBranchP50Milliseconds:F2}ms " +
                $"output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P4M FAIL: {exception}");
            return 1;
        }
    }

    private static RecoveryMatrixRun MeasureRecoveryMatrixOpen(
        string databaseDirectory,
        List<Guid> historyIds,
        int requestedIndex,
        int repetition,
        RecoveryCacheState cacheState)
    {
        var sink = new TimedTraceResearchEventSink();
        var wallClock = Stopwatch.StartNew();
        using var reopened = ChronicleDatabase.Open(databaseDirectory, researchEventSink: sink);
        wallClock.Stop();

        var telemetry = reopened.GetResearchTelemetryStatus();
        var timed = sink.Snapshot();
        var recoveryStarted = timed.Single(item => item.Event.EventKind == ResearchEventKind.RecoveryStarted);
        var recoveryCompleted = timed.Single(item => item.Event.EventKind == ResearchEventKind.RecoveryCompleted);
        var recoveryCompletedMilliseconds = recoveryCompleted.Elapsed.TotalMilliseconds
            - recoveryStarted.Elapsed.TotalMilliseconds;
        var mainHistoryId = recoveryStarted.Event.HistoryId.Value;

        var globalPhases = ExtractRecoveryPhaseDurations(timed, recoveryStarted, mainHistoryId);
        var requestedHistoryId = historyIds[requestedIndex];
        var requestedPhases = ExtractRecoveryPhaseDurations(timed, recoveryStarted, requestedHistoryId);
        var allBranchPhases = historyIds
            .SelectMany(historyId => ExtractRecoveryPhaseDurations(timed, recoveryStarted, historyId))
            .ToArray();

        var requestedStarted = timed.Single(item =>
            item.Event.EventKind == ResearchEventKind.OperationStarted
            && item.Event.HistoryId.Value == requestedHistoryId
            && item.Event.TransactionId is null);
        var requestedValidated = timed.Single(item =>
            item.Event.EventKind == ResearchEventKind.HistoryValidated
            && item.Event.HistoryId.Value == requestedHistoryId
            && item.Event.OperationId == requestedStarted.Event.OperationId);
        var requestedBranchRecoveryMilliseconds = requestedValidated.Elapsed.TotalMilliseconds
            - requestedStarted.Elapsed.TotalMilliseconds;

        return new RecoveryMatrixRun(
            Repetition: repetition,
            CacheState: cacheState,
            WallClockOpenMilliseconds: wallClock.Elapsed.TotalMilliseconds,
            RecoveryCompletedMilliseconds: recoveryCompletedMilliseconds,
            CatalogAndDependencyValidationMilliseconds: SumRecoveryPhase(
                globalPhases,
                ResearchRecoveryPhaseKind.CatalogAndDependencyValidation),
            BranchRuntimesOpenMilliseconds: SumRecoveryPhase(
                globalPhases,
                ResearchRecoveryPhaseKind.BranchRuntimesOpen),
            RequestedBranchRecoveryMilliseconds: requestedBranchRecoveryMilliseconds,
            RequestedCheckpointLoadReplayMilliseconds: SumRecoveryPhase(
                requestedPhases,
                ResearchRecoveryPhaseKind.CheckpointLoadAndReplay),
            RequestedWalReplayMilliseconds: SumRecoveryPhase(
                requestedPhases,
                ResearchRecoveryPhaseKind.WalReplay),
            RequestedPhysicalValidationMilliseconds: SumRecoveryPhase(
                requestedPhases,
                ResearchRecoveryPhaseKind.PhysicalStateValidation),
            TotalBranchCheckpointLoadReplayMilliseconds: SumRecoveryPhase(
                allBranchPhases,
                ResearchRecoveryPhaseKind.CheckpointLoadAndReplay),
            TotalBranchWalReplayMilliseconds: SumRecoveryPhase(
                allBranchPhases,
                ResearchRecoveryPhaseKind.WalReplay),
            TotalBranchPhysicalValidationMilliseconds: SumRecoveryPhase(
                allBranchPhases,
                ResearchRecoveryPhaseKind.PhysicalStateValidation),
            TelemetryComplete: telemetry.IsComplete);
    }

    private static RecoveryPhaseDuration[] ExtractRecoveryPhaseDurations(
        IReadOnlyList<TimedResearchEvent> timed,
        TimedResearchEvent recoveryStarted,
        Guid historyId)
        => timed
            .Where(item => item.Event.HistoryId.Value == historyId
                && item.Event.EventKind is ResearchEventKind.RecoveryPhaseStarted
                    or ResearchEventKind.RecoveryPhaseCompleted)
            .GroupBy(item => item.Event.OperationId)
            .Select(group => group.OrderBy(item => item.Event.LogicalEventId).ToArray())
            .Where(group => group.Length == 2
                && group[0].Event.EventKind == ResearchEventKind.RecoveryPhaseStarted
                && group[1].Event.EventKind == ResearchEventKind.RecoveryPhaseCompleted
                && group[0].Event.RecoveryPhaseObservation is not null
                && group[0].Event.RecoveryPhaseObservation!.Phase == group[1].Event.RecoveryPhaseObservation?.Phase)
            .Select(group => new RecoveryPhaseDuration(
                group[0].Event.RecoveryPhaseObservation!.Phase,
                group[1].Elapsed.TotalMilliseconds - group[0].Elapsed.TotalMilliseconds,
                group[0].Elapsed.TotalMilliseconds - recoveryStarted.Elapsed.TotalMilliseconds,
                group[1].Elapsed.TotalMilliseconds - recoveryStarted.Elapsed.TotalMilliseconds))
            .ToArray();

    private static double SumRecoveryPhase(
        IEnumerable<RecoveryPhaseDuration> phases,
        ResearchRecoveryPhaseKind phase)
        => phases.Where(item => item.Phase == phase).Sum(item => item.DurationMilliseconds);

    private static RecoveryRunSummary SummarizeRecoveryRuns(IReadOnlyList<RecoveryMatrixRun> runs)
    {
        static double P(IReadOnlyList<RecoveryMatrixRun> values, Func<RecoveryMatrixRun, double> selector, double percentile)
        {
            var sorted = values.Select(selector).Order().ToArray();
            return Percentile(sorted, percentile);
        }

        return new RecoveryRunSummary(
            RunCount: runs.Count,
            RecoveryP50Milliseconds: P(runs, run => run.RecoveryCompletedMilliseconds, 0.50),
            RecoveryP95Milliseconds: P(runs, run => run.RecoveryCompletedMilliseconds, 0.95),
            WallClockP50Milliseconds: P(runs, run => run.WallClockOpenMilliseconds, 0.50),
            CatalogP50Milliseconds: P(runs, run => run.CatalogAndDependencyValidationMilliseconds, 0.50),
            BranchRuntimesP50Milliseconds: P(runs, run => run.BranchRuntimesOpenMilliseconds, 0.50),
            RequestedBranchP50Milliseconds: P(runs, run => run.RequestedBranchRecoveryMilliseconds, 0.50),
            RequestedCheckpointP50Milliseconds: P(runs, run => run.RequestedCheckpointLoadReplayMilliseconds, 0.50),
            RequestedWalP50Milliseconds: P(runs, run => run.RequestedWalReplayMilliseconds, 0.50),
            RequestedPhysicalValidationP50Milliseconds: P(runs, run => run.RequestedPhysicalValidationMilliseconds, 0.50));
    }

    private enum RecoveryCacheState : byte
    {
        FreshImageOpen = 1,
        ImmediateWarmReopen = 2,
    }

    private sealed record RecoveryPhaseDuration(
        ResearchRecoveryPhaseKind Phase,
        double DurationMilliseconds,
        double StartedMilliseconds,
        double CompletedMilliseconds);

    private sealed record RecoveryMatrixRun(
        int Repetition,
        RecoveryCacheState CacheState,
        double WallClockOpenMilliseconds,
        double RecoveryCompletedMilliseconds,
        double CatalogAndDependencyValidationMilliseconds,
        double BranchRuntimesOpenMilliseconds,
        double RequestedBranchRecoveryMilliseconds,
        double RequestedCheckpointLoadReplayMilliseconds,
        double RequestedWalReplayMilliseconds,
        double RequestedPhysicalValidationMilliseconds,
        double TotalBranchCheckpointLoadReplayMilliseconds,
        double TotalBranchWalReplayMilliseconds,
        double TotalBranchPhysicalValidationMilliseconds,
        bool TelemetryComplete);

    private sealed record RecoveryRunSummary(
        int RunCount,
        double RecoveryP50Milliseconds,
        double RecoveryP95Milliseconds,
        double WallClockP50Milliseconds,
        double CatalogP50Milliseconds,
        double BranchRuntimesP50Milliseconds,
        double RequestedBranchP50Milliseconds,
        double RequestedCheckpointP50Milliseconds,
        double RequestedWalP50Milliseconds,
        double RequestedPhysicalValidationP50Milliseconds);

    private sealed record RecoveryMatrixPilotResult(
        string Pilot,
        int Seed,
        int HistoryCount,
        int CheckpointCommitsPerHistory,
        int WalTailCommitsPerHistory,
        int ValueBytes,
        int RequestedIndex,
        int Repetitions,
        long SourceCheckpointBytesWrittenByGc,
        long SourceCheckpointFileBytes,
        long SourceWalFileBytes,
        RecoveryRunSummary FreshImage,
        RecoveryRunSummary WarmReopen,
        IReadOnlyList<RecoveryMatrixRun> Runs,
        bool CurrentSemanticsAllowsSelectiveHistoryReady,
        bool FreshImageIsOsColdCacheClaim);
}
