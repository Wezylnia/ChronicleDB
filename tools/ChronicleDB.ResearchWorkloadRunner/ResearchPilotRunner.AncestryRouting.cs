using System.Diagnostics;
using System.Text.Json;
using ChronicleDB;

internal static partial class ResearchPilotRunner
{
    private static int RunStableAncestryRoutingPilot(string[] args)
    {
        if (args.Length < 5
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var depth)
            || !int.TryParse(args[2], out var keyCount)
            || !int.TryParse(args[3], out var reads)
            || depth is < 2 or > 16
            || keyCount is < 32 or > 4096
            || reads < 1_000
            || !TryParseAncestryDistribution(args[4], out var distribution))
        {
            Console.Error.WriteLine(
                "Usage: pilot P3B <seed> <depth:2..16> <key-count:32..4096> <reads>=1000 " +
                "<uniform|zipf> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 6
            ? Path.GetFullPath(args[5])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p3b-{seed}-{depth}-{keyCount}-{distribution}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var sourceDirectory = Path.Combine(outputDirectory, "source");
        var baselineDirectory = Path.Combine(outputDirectory, "baseline");
        var routedDirectory = Path.Combine(outputDirectory, "routed");

        try
        {
            var random = new Random(seed);
            Guid leafBranchId;
            var tombstonedKeys = new HashSet<int>();
            using (var database = ChronicleDatabase.Open(sourceDirectory))
            {
                for (var keyId = 0; keyId < keyCount; keyId++)
                {
                    database.Put(Key(keyId), Payload(64, random, salt: keyId));
                }

                ChronicleBranch? current = null;
                var handles = new List<ChronicleBranch>(depth);
                try
                {
                    for (var currentDepth = 1; currentDepth <= depth; currentDepth++)
                    {
                        current = current is null
                            ? database.CreateBranch($"p3b-{currentDepth:D2}")
                            : current.CreateBranch($"p3b-{currentDepth:D2}");
                        handles.Add(current);

                        if (currentDepth == Math.Max(1, depth / 2) && currentDepth < depth)
                        {
                            for (var keyId = 0; keyId < keyCount; keyId += 4)
                            {
                                if (!current.Delete(Key(keyId)))
                                {
                                    throw new InvalidOperationException("P3B could not create inherited tombstone workload.");
                                }
                                tombstonedKeys.Add(keyId);
                            }
                        }
                    }

                    leafBranchId = current!.BranchId;
                    current.Put(Key(keyCount * 3), Payload(64, random, salt: 0x33));
                }
                finally
                {
                    for (var index = handles.Count - 1; index >= 0; index--)
                    {
                        handles[index].Dispose();
                    }
                }
            }

            CopyDirectory(sourceDirectory, baselineDirectory);
            CopyDirectory(sourceDirectory, routedDirectory);
            var queryIds = BuildAncestryQueryIds(seed, keyCount, reads, distribution);

            AncestryReadTiming baseline;
            using (var database = ChronicleDatabase.Open(baselineDirectory))
            using (var leaf = database.OpenBranch(leafBranchId))
            {
                baseline = MeasureAncestryReadBatch(leaf, queryIds, keyCount, tombstonedKeys);
            }

            var persistentBytesBefore = Directory.EnumerateFiles(routedDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            AncestryReadTiming routeBuild;
            AncestryReadTiming routed;
            AncestryReadTiming local;
            long routeBuildThreadAllocatedBytes;
            ResearchAncestryRoutingDiagnostics routingDiagnostics;
            bool compactionPreservedResults;
            using (var database = ChronicleDatabase.Open(routedDirectory))
            using (var leaf = database.OpenBranch(leafBranchId))
            {
                database.SetResearchAncestryRoutingEnabled(leaf.BranchId, enabled: true);
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                routeBuild = MeasureAncestryReadBatch(leaf, queryIds, keyCount, tombstonedKeys);
                routeBuildThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                routed = MeasureAncestryReadBatch(leaf, queryIds, keyCount, tombstonedKeys);
                local = MeasureRepeatedBranchRead(leaf, Key(keyCount * 3), expectedFound: true, Math.Max(1_000, reads / 10));
                routingDiagnostics = database.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);

                _ = database.RunCompaction();
                compactionPreservedResults = VerifyAncestrySample(
                    leaf,
                    queryIds,
                    keyCount,
                    tombstonedKeys,
                    sampleCount: Math.Min(256, queryIds.Length));
            }
            var persistentBytesAfterReadsAndCompaction = Directory.EnumerateFiles(routedDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);

            bool reopenStartsEmpty;
            bool reopenRebuilds;
            using (var reopened = ChronicleDatabase.Open(routedDirectory))
            using (var leaf = reopened.OpenBranch(leafBranchId))
            {
                var before = reopened.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
                reopenStartsEmpty = !before.Enabled && before.EntryCount == 0;
                reopened.SetResearchAncestryRoutingEnabled(leaf.BranchId, enabled: true);
                var probeId = queryIds.First(id => id < keyCount && !tombstonedKeys.Contains(id));
                if (!leaf.TryGet(Key(probeId), out _))
                {
                    throw new InvalidOperationException("P3B reopen probe unexpectedly missed inherited state.");
                }
                var after = reopened.CaptureResearchAncestryRoutingDiagnostics(leaf.BranchId);
                reopenRebuilds = after.EntryCount == 1 && after.Builds == 1;
            }

            var result = new StableAncestryRoutingPilotResult(
                Pilot: "P3B",
                Seed: seed,
                Depth: depth,
                KeyCount: keyCount,
                Reads: reads,
                Distribution: distribution.ToString(),
                TombstoneKeyCount: tombstonedKeys.Count,
                NegativeQueryFraction: queryIds.Count(id => id >= keyCount) / (double)queryIds.Length,
                Baseline: baseline,
                RouteBuild: routeBuild,
                RoutedWarm: routed,
                LocalWarm: local,
                WarmP99Speedup: Ratio(baseline.P99Nanoseconds, routed.P99Nanoseconds),
                WarmMeanSpeedup: Ratio(baseline.MeanNanoseconds, routed.MeanNanoseconds),
                RouteBuildThreadAllocatedBytes: routeBuildThreadAllocatedBytes,
                RouteEntryCount: routingDiagnostics.EntryCount,
                IndexedKeyBytes: routingDiagnostics.KeyBytes,
                RouteHits: routingDiagnostics.Hits,
                RouteMisses: routingDiagnostics.Misses,
                RouteBuilds: routingDiagnostics.Builds,
                RouteInvalidations: routingDiagnostics.Invalidations,
                ReopenStartsEmpty: reopenStartsEmpty,
                ReopenRebuilds: reopenRebuilds,
                CompactionPreservedResults: compactionPreservedResults,
                PersistentFileBytesBeforeCandidateReads: persistentBytesBefore,
                PersistentFileBytesAfterCandidateCompaction: persistentBytesAfterReadsAndCompaction,
                StoresPhysicalPagePointers: false,
                AuthoritativeRecoveryState: false);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p3b-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));

            var pass = result.ReopenStartsEmpty
                && result.ReopenRebuilds
                && result.CompactionPreservedResults
                && result.RouteEntryCount > 0
                && result.RouteBuilds > 0
                && result.RouteHits > 0
                && result.RouteInvalidations == 0;
            Console.WriteLine(
                $"P3B {(pass ? "PASS" : "FAIL")} depth={depth} keys={keyCount} reads={reads} dist={distribution} " +
                $"baseline-p99={baseline.P99Nanoseconds:F0}ns routed-p99={routed.P99Nanoseconds:F0}ns " +
                $"speedup={result.WarmP99Speedup:F2}x routes={result.RouteEntryCount} hits={result.RouteHits} " +
                $"reopen={result.ReopenRebuilds} compaction={result.CompactionPreservedResults} output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P3B FAIL: {exception}");
            return 1;
        }
    }

    private static int[] BuildAncestryQueryIds(
        int seed,
        int keyCount,
        int reads,
        AncestryQueryDistribution distribution)
    {
        var random = new Random(seed ^ 0x5A17_3C21);
        var ids = new int[reads];
        var universe = checked(keyCount * 2);
        for (var index = 0; index < ids.Length; index++)
        {
            ids[index] = distribution switch
            {
                AncestryQueryDistribution.Uniform => random.Next(universe),
                AncestryQueryDistribution.ZipfLike => Math.Min(
                    universe - 1,
                    (int)(Math.Pow(random.NextDouble(), 3.0) * universe)),
                _ => throw new InvalidOperationException("Unknown ancestry query distribution."),
            };
        }
        return ids;
    }

    private static AncestryReadTiming MeasureAncestryReadBatch(
        ChronicleBranch leaf,
        int[] queryIds,
        int keyCount,
        HashSet<int> tombstonedKeys)
    {
        var samples = new double[queryIds.Length];
        for (var index = 0; index < queryIds.Length; index++)
        {
            var keyId = queryIds[index];
            var expectedFound = keyId < keyCount && !tombstonedKeys.Contains(keyId);
            var started = Stopwatch.GetTimestamp();
            var found = leaf.TryGet(Key(keyId), out _);
            var elapsed = Stopwatch.GetTimestamp() - started;
            if (found != expectedFound)
            {
                throw new InvalidOperationException($"P3B visibility mismatch for key {keyId}.");
            }
            samples[index] = elapsed * 1_000_000_000d / Stopwatch.Frequency;
        }
        Array.Sort(samples);
        return new AncestryReadTiming(
            P50Nanoseconds: Percentile(samples, 0.50),
            P95Nanoseconds: Percentile(samples, 0.95),
            P99Nanoseconds: Percentile(samples, 0.99),
            MeanNanoseconds: samples.Average());
    }

    private static AncestryReadTiming MeasureRepeatedBranchRead(
        ChronicleBranch branch,
        byte[] key,
        bool expectedFound,
        int reads)
    {
        var samples = new double[reads];
        for (var index = 0; index < reads; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var found = branch.TryGet(key, out _);
            var elapsed = Stopwatch.GetTimestamp() - started;
            if (found != expectedFound)
            {
                throw new InvalidOperationException("P3B local read visibility mismatch.");
            }
            samples[index] = elapsed * 1_000_000_000d / Stopwatch.Frequency;
        }
        Array.Sort(samples);
        return new AncestryReadTiming(
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples.Average());
    }

    private static bool VerifyAncestrySample(
        ChronicleBranch leaf,
        int[] queryIds,
        int keyCount,
        HashSet<int> tombstonedKeys,
        int sampleCount)
    {
        for (var index = 0; index < sampleCount; index++)
        {
            var keyId = queryIds[index];
            var expectedFound = keyId < keyCount && !tombstonedKeys.Contains(keyId);
            if (leaf.TryGet(Key(keyId), out _) != expectedFound)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseAncestryDistribution(string value, out AncestryQueryDistribution distribution)
    {
        if (value.Equals("uniform", StringComparison.OrdinalIgnoreCase))
        {
            distribution = AncestryQueryDistribution.Uniform;
            return true;
        }
        if (value.Equals("zipf", StringComparison.OrdinalIgnoreCase)
            || value.Equals("zipf-like", StringComparison.OrdinalIgnoreCase))
        {
            distribution = AncestryQueryDistribution.ZipfLike;
            return true;
        }
        distribution = default;
        return false;
    }

    private enum AncestryQueryDistribution : byte
    {
        Uniform = 1,
        ZipfLike = 2,
    }

    private sealed record AncestryReadTiming(
        double P50Nanoseconds,
        double P95Nanoseconds,
        double P99Nanoseconds,
        double MeanNanoseconds);

    private sealed record StableAncestryRoutingPilotResult(
        string Pilot,
        int Seed,
        int Depth,
        int KeyCount,
        int Reads,
        string Distribution,
        int TombstoneKeyCount,
        double NegativeQueryFraction,
        AncestryReadTiming Baseline,
        AncestryReadTiming RouteBuild,
        AncestryReadTiming RoutedWarm,
        AncestryReadTiming LocalWarm,
        double WarmP99Speedup,
        double WarmMeanSpeedup,
        long RouteBuildThreadAllocatedBytes,
        int RouteEntryCount,
        long IndexedKeyBytes,
        long RouteHits,
        long RouteMisses,
        long RouteBuilds,
        long RouteInvalidations,
        bool ReopenStartsEmpty,
        bool ReopenRebuilds,
        bool CompactionPreservedResults,
        long PersistentFileBytesBeforeCandidateReads,
        long PersistentFileBytesAfterCandidateCompaction,
        bool StoresPhysicalPagePointers,
        bool AuthoritativeRecoveryState);
}
