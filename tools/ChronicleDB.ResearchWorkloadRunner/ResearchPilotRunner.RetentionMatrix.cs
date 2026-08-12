using System.Text.Json;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;

internal static partial class ResearchPilotRunner
{
    private static int RunRetentionMatrixPilot(string[] args)
    {
        if (args.Length < 8
            || !int.TryParse(args[0], out var seedStart)
            || !int.TryParse(args[1], out var seedCount)
            || !int.TryParse(args[2], out var baseKeyCount)
            || !TryParsePositiveIntList(args[3], out var valueSizes)
            || !TryParsePositiveIntList(args[4], out var churnRounds)
            || !TryParsePositiveIntList(args[5], out var hotKeyCounts)
            || !TryParsePositiveIntList(args[6], out var fanouts)
            || !int.TryParse(args[7], out var privateBytes)
            || seedCount is < 1 or > 100
            || baseKeyCount <= 0
            || privateBytes <= 0
            || hotKeyCounts.Any(value => value > baseKeyCount)
            || fanouts.Any(value => value > 64))
        {
            Console.Error.WriteLine(
                "Usage: pilot P1M <seed-start> <seed-count:1..100> <base-key-count> <value-bytes-csv> " +
                "<churn-rounds-csv> <hot-key-count-csv> <fanout-csv:1..64> <private-bytes> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 9
            ? Path.GetFullPath(args[8])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p1m-{seedStart}-{seedCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var cases = new List<RetentionMatrixCaseResult>();
            var ordinal = 0;
            for (var seedOffset = 0; seedOffset < seedCount; seedOffset++)
            {
                var seed = checked(seedStart + seedOffset);
                foreach (var valueBytes in valueSizes)
                {
                    foreach (var rounds in churnRounds)
                    {
                        foreach (var hotKeyCount in hotKeyCounts)
                        {
                            foreach (var fanout in fanouts)
                            {
                                ordinal++;
                                var caseDirectory = Path.Combine(outputDirectory, $"case-{ordinal:D4}");
                                var result = RunRetentionMatrixCase(
                                    seed,
                                    baseKeyCount,
                                    valueBytes,
                                    rounds,
                                    hotKeyCount,
                                    fanout,
                                    privateBytes,
                                    caseDirectory);
                                cases.Add(result);
                            }
                        }
                    }
                }
            }

            var ratios = cases
                .Where(item => item.SetMarginalPayloadBytes > 0)
                .Select(item => item.CoarseToExactAmplification)
                .Order()
                .ToArray();
            var physicalRatios = cases
                .Where(item => item.SetMarginalPayloadBytes > 0 && item.AllocationMeasurementExact)
                .Select(item => item.SharedPhysicalToExactMarginalRatio)
                .Order()
                .ToArray();
            var resultSet = new RetentionMatrixPilotResult(
                Pilot: "P1M",
                SeedStart: seedStart,
                SeedCount: seedCount,
                BaseKeyCount: baseKeyCount,
                CaseCount: cases.Count,
                Cases: cases,
                ExactOracleMismatchCount: cases.Count(item => !item.ExactMatchesExpected),
                CoarseOracleMismatchCount: cases.Count(item => !item.CoarseMatchesExpected),
                NonAdditivityMismatchCount: cases.Count(item => !item.NonAdditivityMatchesExpected),
                PhysicalMeasurementIncompleteCount: cases.Count(item => !item.AllocationMeasurementExact),
                MedianCoarseToExactAmplification: Percentile(ratios, 0.50),
                P95CoarseToExactAmplification: Percentile(ratios, 0.95),
                MedianSharedPhysicalToExactRatio: Percentile(physicalRatios, 0.50),
                P95SharedPhysicalToExactRatio: Percentile(physicalRatios, 0.95));
            File.WriteAllText(
                Path.Combine(outputDirectory, "p1m-result.json"),
                JsonSerializer.Serialize(resultSet, JsonOptions));

            var pass = resultSet.ExactOracleMismatchCount == 0
                && resultSet.CoarseOracleMismatchCount == 0
                && resultSet.NonAdditivityMismatchCount == 0
                && resultSet.PhysicalMeasurementIncompleteCount == 0;
            Console.WriteLine(
                $"P1M {(pass ? "PASS" : "FAIL")} cases={resultSet.CaseCount} " +
                $"coarse-median={resultSet.MedianCoarseToExactAmplification:F2} " +
                $"coarse-p95={resultSet.P95CoarseToExactAmplification:F2} " +
                $"physical-median={resultSet.MedianSharedPhysicalToExactRatio:F2} " +
                $"output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1M FAIL: {exception}");
            return 1;
        }
    }

    private static RetentionMatrixCaseResult RunRetentionMatrixCase(
        int seed,
        int baseKeyCount,
        int valueBytes,
        int churnRounds,
        int hotKeyCount,
        int fanout,
        int privateBytes,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var sourceDirectory = Path.Combine(outputDirectory, "source");
        var retainedDirectory = Path.Combine(outputDirectory, "retained");
        var droppedDirectory = Path.Combine(outputDirectory, "dropped");
        var random = new Random(seed);
        var branchIds = new List<Guid>(fanout);
        var branchHistoryIds = new List<Guid>(fanout);
        long targetSingleMarginal;
        long setMarginal;
        long coarsePayload;
        long branchPrivateLogicalBytes;

        using (var database = ChronicleDatabase.Open(sourceDirectory))
        {
            for (var keyId = 0; keyId < baseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(valueBytes, random, salt: keyId));
            }

            var branches = new List<ChronicleBranch>(fanout);
            try
            {
                for (var index = 0; index < fanout; index++)
                {
                    var branch = database.CreateBranch($"p1m-{seed}-branch-{index:D2}");
                    branches.Add(branch);
                    branchIds.Add(branch.BranchId);
                    branchHistoryIds.Add(branch.HistoryId);
                    branch.Put(
                        Key(baseKeyCount + 1 + index),
                        Payload(privateBytes, random, salt: checked(0x7000 + index)));
                }

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
                var roots = database.GetHistoryTopologyDiagnostics().RetentionRoots
                    .Where(root => root.Kind.Equals("BranchBase", StringComparison.Ordinal)
                        && branchHistoryIds.Contains(root.OwnerHistoryId))
                    .OrderBy(root => root.RootId)
                    .ToArray();
                if (roots.Length != fanout)
                {
                    throw new InvalidOperationException("P1M could not resolve every branch-base root.");
                }

                var inspector = new RetentionInspector(evaluationSnapshot);
                targetSingleMarginal = inspector.WhatIfDrop(roots[0].RootId).MarginalPayloadBytes;
                setMarginal = inspector.WhatIfDrop(roots.Select(root => root.RootId).ToArray()).MarginalPayloadBytes;
                coarsePayload = CoarseOldestRootRetentionAnalyzer.Analyze(evaluationSnapshot).RootInducedPayloadBytes;
                branchPrivateLogicalBytes = rawSnapshot.Histories
                    .Where(history => branchHistoryIds.Contains(history.HistoryId))
                    .SelectMany(history => history.Versions)
                    .Sum(version => version.LogicalPayloadBytes);

                _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            }
            finally
            {
                foreach (var branch in branches.AsEnumerable().Reverse())
                {
                    branch.Dispose();
                }
            }
        }

        CopyDirectory(sourceDirectory, retainedDirectory);
        CopyDirectory(sourceDirectory, droppedDirectory);

        using (var retained = ChronicleDatabase.Open(retainedDirectory))
        {
            _ = retained.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            _ = retained.RunCompaction();
        }

        using (var dropped = ChronicleDatabase.Open(droppedDirectory))
        {
            foreach (var branchId in branchIds.AsEnumerable().Reverse())
            {
                dropped.DeleteBranch(branchId);
            }
            _ = dropped.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            _ = dropped.RunCompaction();
        }

        var retainedPhysical = PhysicalStorageProbe.Capture(retainedDirectory);
        var droppedPhysical = PhysicalStorageProbe.Capture(droppedDirectory);
        var retainedBranchLocalAllocated = retainedPhysical.Files
            .Where(file => branchIds.Any(branchId =>
                file.RelativePath.Contains(branchId.ToString("N"), StringComparison.OrdinalIgnoreCase)))
            .Sum(file => file.AllocatedBytes);
        var pairedAllocatedDifference = Math.Max(0, retainedPhysical.AllocatedBytes - droppedPhysical.AllocatedBytes);
        var sharedPhysicalDifference = Math.Max(0, pairedAllocatedDifference - retainedBranchLocalAllocated);
        var expectedSetMarginal = checked((long)hotKeyCount * valueBytes);
        var expectedCoarse = checked(expectedSetMarginal * churnRounds);
        var expectedSingleMarginal = fanout == 1 ? expectedSetMarginal : 0L;
        var coarseAmplification = setMarginal <= 0 ? 0d : (double)coarsePayload / setMarginal;
        var sharedPhysicalRatio = setMarginal <= 0 ? 0d : (double)sharedPhysicalDifference / setMarginal;
        double? hra = branchPrivateLogicalBytes <= 0 ? null : (double)setMarginal / branchPrivateLogicalBytes;
        var result = new RetentionMatrixCaseResult(
            seed,
            baseKeyCount,
            valueBytes,
            churnRounds,
            hotKeyCount,
            (double)hotKeyCount / baseKeyCount,
            fanout,
            privateBytes,
            branchPrivateLogicalBytes,
            targetSingleMarginal,
            setMarginal,
            coarsePayload,
            coarseAmplification,
            hra,
            pairedAllocatedDifference,
            retainedBranchLocalAllocated,
            sharedPhysicalDifference,
            sharedPhysicalRatio,
            retainedPhysical.AllocationIsExact && droppedPhysical.AllocationIsExact,
            ExactMatchesExpected: setMarginal == expectedSetMarginal,
            CoarseMatchesExpected: coarsePayload == expectedCoarse,
            NonAdditivityMatchesExpected: targetSingleMarginal == expectedSingleMarginal);
        File.WriteAllText(
            Path.Combine(outputDirectory, "case-result.json"),
            JsonSerializer.Serialize(result, JsonOptions));
        return result;
    }

    private static bool TryParsePositiveIntList(string value, out int[] values)
    {
        values = [];
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var parsed = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out parsed[index]) || parsed[index] <= 0)
            {
                return false;
            }
        }

        values = parsed.Distinct().Order().ToArray();
        return true;
    }

    private sealed record RetentionMatrixCaseResult(
        int Seed,
        int BaseKeyCount,
        int ValueBytes,
        int ChurnRounds,
        int HotKeyCount,
        double HotKeyFraction,
        int Fanout,
        int PrivateBytes,
        long BranchPrivateLogicalBytes,
        long TargetSingleRootMarginalPayloadBytes,
        long SetMarginalPayloadBytes,
        long CoarseRootInducedPayloadBytes,
        double CoarseToExactAmplification,
        double? HiddenRetentionAmplification,
        long PairedAllocatedByteDifference,
        long RetainedBranchLocalAllocatedBytes,
        long PairedSharedAllocatedByteDifference,
        double SharedPhysicalToExactMarginalRatio,
        bool AllocationMeasurementExact,
        bool ExactMatchesExpected,
        bool CoarseMatchesExpected,
        bool NonAdditivityMatchesExpected);

    private sealed record RetentionMatrixPilotResult(
        string Pilot,
        int SeedStart,
        int SeedCount,
        int BaseKeyCount,
        int CaseCount,
        IReadOnlyList<RetentionMatrixCaseResult> Cases,
        int ExactOracleMismatchCount,
        int CoarseOracleMismatchCount,
        int NonAdditivityMismatchCount,
        int PhysicalMeasurementIncompleteCount,
        double MedianCoarseToExactAmplification,
        double P95CoarseToExactAmplification,
        double MedianSharedPhysicalToExactRatio,
        double P95SharedPhysicalToExactRatio);
}
