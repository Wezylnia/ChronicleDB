using System.Diagnostics;
using System.Text.Json;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;

internal static partial class ResearchPilotRunner
{
    private static int RunRetentionInterferencePilot(string[] args)
    {
        if (args.Length < 7
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var baseKeyCount)
            || !int.TryParse(args[2], out var valueBytes)
            || !int.TryParse(args[3], out var churnRounds)
            || !int.TryParse(args[4], out var hotKeyCount)
            || !int.TryParse(args[5], out var privateBytes)
            || !int.TryParse(args[6], out var readBudget)
            || baseKeyCount < 16
            || valueBytes <= 0
            || churnRounds <= 0
            || hotKeyCount <= 0
            || hotKeyCount > baseKeyCount
            || privateBytes <= 0
            || readBudget < 1_000)
        {
            Console.Error.WriteLine(
                "Usage: pilot P1I <seed> <base-key-count>=16 <value-bytes> <churn-rounds> " +
                "<hot-key-count> <private-bytes> <read-budget>=1000 [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 8
            ? Path.GetFullPath(args[7])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p1i-{seed}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var sourceDirectory = Path.Combine(outputDirectory, "source");
        var treatmentDirectory = Path.Combine(outputDirectory, "treatment");
        var controlDirectory = Path.Combine(outputDirectory, "control");

        try
        {
            var random = new Random(seed);
            Guid branchId;
            long exactMarginalPayloadBytes;
            long coarseRootPayloadBytes;
            using (var database = ChronicleDatabase.Open(sourceDirectory))
            {
                for (var keyId = 0; keyId < baseKeyCount; keyId++)
                {
                    database.Put(Key(keyId), Payload(valueBytes, random, salt: keyId));
                }

                using var branch = database.CreateBranch("p1-interference-old-branch");
                branchId = branch.BranchId;
                branch.Put(Key(baseKeyCount + 1), Payload(privateBytes, random, salt: 0x791));

                var hotKeys = Enumerable.Range(0, baseKeyCount)
                    .OrderBy(_ => random.Next())
                    .Take(hotKeyCount)
                    .Order()
                    .ToArray();
                for (var round = 1; round <= churnRounds; round++)
                {
                    foreach (var keyId in hotKeys)
                    {
                        database.Put(
                            Key(keyId),
                            Payload(valueBytes, random, salt: checked(keyId + (round * 100_000))));
                    }
                }

                var rawSnapshot = database.CaptureResearchRetentionSnapshot();
                var evaluationSnapshot = rawSnapshot with
                {
                    Histories = rawSnapshot.Histories
                        .Select(history => history with { RetentionFloor = history.CurrentSequence })
                        .ToArray(),
                };
                var root = database.GetHistoryTopologyDiagnostics().RetentionRoots.Single(item =>
                    item.Kind.Equals("BranchBase", StringComparison.Ordinal)
                    && item.OwnerHistoryId == branch.HistoryId);
                exactMarginalPayloadBytes = new RetentionInspector(evaluationSnapshot)
                    .WhatIfDrop(root.RootId)
                    .MarginalPayloadBytes;
                coarseRootPayloadBytes = CoarseOldestRootRetentionAnalyzer.Analyze(evaluationSnapshot)
                    .RootInducedPayloadBytes;

                // Normalize logical retention before cloning. Treatment and control start
                // from byte-identical durable state; only treatment drops the old branch.
                _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            }

            CopyDirectory(sourceDirectory, treatmentDirectory);
            CopyDirectory(sourceDirectory, controlDirectory);

            var treatmentBefore = PhysicalStorageProbe.Capture(treatmentDirectory);
            CompactionResult? compaction = null;
            GarbageCollectionResult? gc = null;
            double maintenanceMilliseconds = 0;
            LatencySeries treatment;
            using (var database = ChronicleDatabase.Open(treatmentDirectory))
            {
                WarmMainReads(database, baseKeyCount, valueBytes, Math.Min(512, readBudget / 4));
                using var start = new ManualResetEventSlim(initialState: false);
                var maintenance = Task.Run(() =>
                {
                    start.Wait();
                    var stopwatch = Stopwatch.StartNew();
                    database.DeleteBranch(branchId);
                    gc = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
                    compaction = database.RunCompaction();
                    stopwatch.Stop();
                    maintenanceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                });

                start.Set();
                treatment = MeasureMainReadsDuringTask(
                    database,
                    baseKeyCount,
                    valueBytes,
                    readBudget,
                    maintenance);
                maintenance.GetAwaiter().GetResult();
            }

            if (compaction is null || gc is null)
            {
                throw new InvalidOperationException("P1I maintenance operation did not complete.");
            }

            var treatmentAfter = PhysicalStorageProbe.Capture(treatmentDirectory);
            LatencySeries control;
            using (var database = ChronicleDatabase.Open(controlDirectory))
            {
                WarmMainReads(database, baseKeyCount, valueBytes, Math.Min(512, readBudget / 4));
                control = MeasureMainReadsFixed(
                    database,
                    baseKeyCount,
                    valueBytes,
                    treatment.SampleCount);
            }

            var result = new RetentionInterferencePilotResult(
                Pilot: "P1I",
                Seed: seed,
                BaseKeyCount: baseKeyCount,
                ValueBytes: valueBytes,
                ChurnRounds: churnRounds,
                HotKeyCount: hotKeyCount,
                HotKeyFraction: (double)hotKeyCount / baseKeyCount,
                PrivateBytes: privateBytes,
                ReadBudget: readBudget,
                SamplesDuringTreatment: treatment.SampleCount,
                ExactMarginalPayloadBytes: exactMarginalPayloadBytes,
                CoarseRootInducedPayloadBytes: coarseRootPayloadBytes,
                Treatment: treatment,
                Control: control,
                P99InterferenceRatio: Ratio(treatment.P99Nanoseconds, control.P99Nanoseconds),
                P95InterferenceRatio: Ratio(treatment.P95Nanoseconds, control.P95Nanoseconds),
                MaximumPauseRatio: Ratio(treatment.MaximumNanoseconds, control.MaximumNanoseconds),
                MaintenanceElapsedMilliseconds: maintenanceMilliseconds,
                GcVersionsReclaimed: gc.VersionsReclaimed,
                GcCheckpointBytesWritten: gc.CheckpointBytesWritten,
                CompactionBytesRewritten: compaction.BytesRewritten,
                CompactionBytesReclaimed: compaction.BytesReclaimed,
                AllocatedBytesBefore: treatmentBefore.AllocatedBytes,
                AllocatedBytesAfter: treatmentAfter.AllocatedBytes,
                AllocatedBytesReleased: Math.Max(0, treatmentBefore.AllocatedBytes - treatmentAfter.AllocatedBytes),
                AllocationMeasurementExact: treatmentBefore.AllocationIsExact && treatmentAfter.AllocationIsExact);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p1i-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));

            var pass = treatment.SampleCount >= Math.Min(1_000, readBudget)
                && treatment.AllReadsValid
                && control.AllReadsValid
                && exactMarginalPayloadBytes > 0
                && result.AllocationMeasurementExact;
            Console.WriteLine(
                $"P1I {(pass ? "PASS" : "FAIL")} samples={treatment.SampleCount} " +
                $"p99={result.P99InterferenceRatio:F2}x p95={result.P95InterferenceRatio:F2}x " +
                $"max={result.MaximumPauseRatio:F2}x maintenance={maintenanceMilliseconds:F2}ms " +
                $"rewritten={compaction.BytesRewritten} reclaimed={compaction.BytesReclaimed} " +
                $"output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1I FAIL: {exception}");
            return 1;
        }
    }

    private static void WarmMainReads(ChronicleDatabase database, int keyCount, int expectedValueBytes, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var found = database.TryGet(Key(index % keyCount), out var value);
            if (!found || value.Length != expectedValueBytes)
            {
                throw new InvalidOperationException("P1I warmup observed an unexpected Main value.");
            }
        }
    }

    private static LatencySeries MeasureMainReadsDuringTask(
        ChronicleDatabase database,
        int keyCount,
        int expectedValueBytes,
        int readBudget,
        Task concurrentTask)
    {
        var samples = new List<double>(readBudget);
        var valid = true;
        var started = Stopwatch.GetTimestamp();
        var minimumSamples = Math.Min(1_000, readBudget);
        while (samples.Count < readBudget && (!concurrentTask.IsCompleted || samples.Count < minimumSamples))
        {
            valid &= MeasureMainRead(database, keyCount, expectedValueBytes, samples.Count, samples);
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        return SummarizeLatency(samples, valid, elapsed);
    }

    private static LatencySeries MeasureMainReadsFixed(
        ChronicleDatabase database,
        int keyCount,
        int expectedValueBytes,
        int count)
    {
        var samples = new List<double>(count);
        var valid = true;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++)
        {
            valid &= MeasureMainRead(database, keyCount, expectedValueBytes, index, samples);
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        return SummarizeLatency(samples, valid, elapsed);
    }

    private static bool MeasureMainRead(
        ChronicleDatabase database,
        int keyCount,
        int expectedValueBytes,
        int ordinal,
        List<double> samples)
    {
        var started = Stopwatch.GetTimestamp();
        var found = database.TryGet(Key(ordinal % keyCount), out var value);
        var elapsed = Stopwatch.GetTimestamp() - started;
        samples.Add(elapsed * 1_000_000_000d / Stopwatch.Frequency);
        return found && value.Length == expectedValueBytes;
    }

    private static LatencySeries SummarizeLatency(List<double> samples, bool allReadsValid, long elapsedTicks)
    {
        var sorted = samples.Order().ToArray();
        var elapsedSeconds = elapsedTicks <= 0 ? 0d : (double)elapsedTicks / Stopwatch.Frequency;
        return new LatencySeries(
            samples.Count,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted.Length == 0 ? 0d : sorted[^1],
            sorted.Length == 0 ? 0d : sorted.Average(),
            elapsedSeconds <= 0 ? 0d : samples.Count / elapsedSeconds,
            allReadsValid);
    }

    private sealed record RetentionInterferencePilotResult(
        string Pilot,
        int Seed,
        int BaseKeyCount,
        int ValueBytes,
        int ChurnRounds,
        int HotKeyCount,
        double HotKeyFraction,
        int PrivateBytes,
        int ReadBudget,
        int SamplesDuringTreatment,
        long ExactMarginalPayloadBytes,
        long CoarseRootInducedPayloadBytes,
        LatencySeries Treatment,
        LatencySeries Control,
        double P99InterferenceRatio,
        double P95InterferenceRatio,
        double MaximumPauseRatio,
        double MaintenanceElapsedMilliseconds,
        int GcVersionsReclaimed,
        long GcCheckpointBytesWritten,
        long CompactionBytesRewritten,
        long CompactionBytesReclaimed,
        long AllocatedBytesBefore,
        long AllocatedBytesAfter,
        long AllocatedBytesReleased,
        bool AllocationMeasurementExact);

    private sealed record LatencySeries(
        int SampleCount,
        double P50Nanoseconds,
        double P95Nanoseconds,
        double P99Nanoseconds,
        double MaximumNanoseconds,
        double MeanNanoseconds,
        double ReadsPerSecond,
        bool AllReadsValid);
}
