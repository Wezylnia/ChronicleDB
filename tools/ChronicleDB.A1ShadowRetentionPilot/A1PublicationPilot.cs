using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronicleDB.Diagnostics.Research;

internal static class A1PublicationPilot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static int RunCase(string[] args)
    {
        if (args.Length != 8
            || !int.TryParse(args[1], out var keyCount)
            || !int.TryParse(args[2], out var branchCountOrDepth)
            || !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var shadowFraction)
            || !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var tombstoneFraction)
            || !int.TryParse(args[5], out var valueBytes)
            || !int.TryParse(args[6], out var seed)
            || keyCount is < 100 or > 65536
            || branchCountOrDepth is < 1 or > 64
            || !double.IsFinite(shadowFraction) || shadowFraction is < 0d or > 1d
            || !double.IsFinite(tombstoneFraction) || tombstoneFraction is < 0d or > 1d
            || valueBytes <= 0)
        {
            Console.Error.WriteLine(
                "Usage: --publication-case <staggered-wide|nested-chain> <keys> <branches-or-depth> " +
                "<shadow-fraction:[0,1]> <tombstone-fraction:[0,1]> <value-bytes> <seed> <output-directory>");
            return 2;
        }

        var topology = args[0];
        var outputDirectory = Path.GetFullPath(args[7]);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            ResearchRetentionSnapshot snapshot;
            ShadowRetentionEffectPrediction expected;
            int realizedShadowKeyCount;
            int realizedTombstoneKeyCount;

            if (topology.Equals("staggered-wide", StringComparison.Ordinal))
            {
                (snapshot, realizedShadowKeyCount, realizedTombstoneKeyCount) = BuildWideSnapshot(
                    keyCount,
                    branchCountOrDepth,
                    shadowFraction,
                    tombstoneFraction,
                    valueBytes,
                    seed);
                var realizedShadowFraction = (double)realizedShadowKeyCount / keyCount;
                var realizedTombstoneFraction = realizedShadowKeyCount == 0
                    ? 0d
                    : (double)realizedTombstoneKeyCount / realizedShadowKeyCount;
                expected = ShadowRetentionEffectModel.Predict(
                    keyCount,
                    branchCountOrDepth,
                    realizedShadowFraction,
                    realizedTombstoneFraction,
                    valueBytes);
            }
            else if (topology.Equals("nested-chain", StringComparison.Ordinal))
            {
                if (branchCountOrDepth > 16 || tombstoneFraction != 0d)
                {
                    throw new InvalidOperationException(
                        "Nested publication cases require depth <= 16 and tombstoneFraction=0 in the frozen plan.");
                }

                (snapshot, realizedShadowKeyCount) = BuildNestedSnapshot(
                    keyCount,
                    branchCountOrDepth,
                    shadowFraction,
                    valueBytes,
                    seed);
                realizedTombstoneKeyCount = 0;
                expected = ShadowRetentionEffectModel.PredictNested(
                    keyCount,
                    branchCountOrDepth,
                    (double)realizedShadowKeyCount / keyCount,
                    valueBytes);
            }
            else
            {
                throw new InvalidOperationException($"Unknown publication topology '{topology}'.");
            }

            _ = AnalyzeAndValidate(snapshot, expected);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var result = AnalyzeAndValidate(snapshot, expected);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            var artifact = new PublicationCaseResult(
                Pilot: "A1-SHADOW-PUBLICATION-CASE",
                Topology: topology,
                Seed: seed,
                KeyCount: keyCount,
                BranchCountOrDepth: branchCountOrDepth,
                RequestedShadowFraction: shadowFraction,
                RequestedTombstoneFraction: tombstoneFraction,
                RealizedShadowKeyCount: realizedShadowKeyCount,
                RealizedTombstoneKeyCount: realizedTombstoneKeyCount,
                VersionCount: snapshot.Histories.Sum(history => history.Versions.Count),
                BaselineVersionCount: result.BaselineVersionCount,
                ShadowAwareVersionCount: result.ShadowAwareVersionCount,
                ExpectedReleasedPayloadBytes: (long)expected.ReleasedParentPayloadBytes,
                MeasuredReleasedPayloadBytes: result.ShadowReleasedPayloadBytes,
                ExpectedReclamationRatio: expected.ShadowAwareReclamationRatio,
                MeasuredReclamationRatio: result.ShadowAwareReclamationRatio,
                FlatExactBaselineVerified: result.FlatExactBaselineVerified,
                CandidateSubsetVerified: result.CandidateIsSubsetOfBaseline,
                ObserverEquivalenceVerified: result.ObserverEquivalenceVerified,
                ObserverMinimalityVerified: result.ObserverMinimalityVerified,
                ObserverEquivalenceCheckCount: result.ObserverEquivalenceCheckCount,
                ObserverKeyResolutionCount: result.ObserverKeyResolutionCount,
                ParentFallbackHops: result.ParentFallbackHops,
                VerifiedProjectionMilliseconds: elapsed,
                ConstructionMilliseconds: result.ConstructionMilliseconds,
                CoreProjectionMilliseconds: result.CoreProjectionMilliseconds,
                ObserverVerificationMilliseconds: result.ObserverVerificationMilliseconds,
                ThreadAllocatedBytes: allocated);
            var resultPath = Path.Combine(outputDirectory, "publication-case-result.json");
            WriteCreateNew(resultPath, JsonSerializer.Serialize(artifact, JsonOptions) + Environment.NewLine);
            Console.WriteLine(
                $"A1-SHADOW-PUBLICATION-CASE PASS topology={topology} seed={seed} keys={keyCount} " +
                $"n={branchCountOrDepth} shadow={(double)realizedShadowKeyCount / keyCount:P3} " +
                $"SAR={artifact.MeasuredReclamationRatio:F3}x ms={artifact.VerifiedProjectionMilliseconds:F2} " +
                $"output={outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"A1-SHADOW-PUBLICATION-CASE FAIL: {exception}");
            return 1;
        }
    }

    public static int RunPilotA(string[] args, bool smoke)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine(
                smoke
                    ? "Usage: --pilot-a-smoke <sealed-plan-directory> [output-directory]"
                    : "Usage: --run-pilot-a <sealed-plan-directory> [output-directory]");
            return 2;
        }

        var planDirectory = Path.GetFullPath(args[0]);
        var outputDirectory = args.Length == 2
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                planDirectory,
                smoke ? $"pilot-a-smoke-{Guid.NewGuid():N}" : "pilot-a");

        try
        {
            var (plan, planHash) = ReadAndVerifyPlan(planDirectory);
            Directory.CreateDirectory(outputDirectory);

            var canonicalExecutionPlan = ShadowRetentionPilotExecutionPlan.Create(plan);
            var executionArtifact = ShadowRetentionPilotExecutionPlanWriter.Write(
                Path.Combine(outputDirectory, "registration"),
                canonicalExecutionPlan);
            var trialPlan = smoke
                ? BuildSmokeTrials(canonicalExecutionPlan, plan.PilotSweepSeed)
                : canonicalExecutionPlan.Runs.ToArray();

            var executions = new List<PilotATrialExecution>(trialPlan.Length);
            foreach (var trial in trialPlan.OrderBy(item => item.TrialOrder))
            {
                var runDirectory = Path.Combine(
                    outputDirectory,
                    "runs",
                    $"trial-{trial.TrialOrder:D4}-{trial.CaseId}-seed-{trial.Seed}-rep-{trial.ProcessRepetition:D2}");
                Directory.CreateDirectory(runDirectory);
                var resultPath = Path.Combine(runDirectory, "publication-case-result.json");

                if (File.Exists(resultPath))
                {
                    var resumedResult = ReadAndValidateExistingCaseResult(trial, resultPath);
                    executions.Add(new PilotATrialExecution(
                        trial.RunId,
                        trial.Tier,
                        trial.TrialOrder,
                        trial.CaseId,
                        trial.Seed,
                        trial.ProcessRepetition,
                        0,
                        Sha256(File.ReadAllBytes(resultPath)),
                        resumedResult.MeasuredReclamationRatio,
                        resumedResult.MeasuredReleasedPayloadBytes,
                        resumedResult.VerifiedProjectionMilliseconds,
                        "RESUMED: verified immutable existing result artifact.",
                        string.Empty));
                    continue;
                }

                if (Directory.EnumerateFileSystemEntries(runDirectory).Any())
                {
                    throw new InvalidOperationException(
                        $"Pilot-A run directory is non-empty without a completed immutable result for run '{trial.RunId}'.");
                }

                var childArgs = new[]
                {
                    "--publication-case",
                    trial.TopologyKind,
                    trial.KeyCount.ToString(CultureInfo.InvariantCulture),
                    trial.BranchCountOrDepth.ToString(CultureInfo.InvariantCulture),
                    trial.ShadowFraction.ToString("R", CultureInfo.InvariantCulture),
                    trial.TombstoneFraction.ToString("R", CultureInfo.InvariantCulture),
                    plan.ValueBytes.ToString(CultureInfo.InvariantCulture),
                    trial.Seed.ToString(CultureInfo.InvariantCulture),
                    runDirectory,
                };

                var child = RunChild(childArgs);
                PublicationCaseResult? caseResult = null;
                string? resultHash = null;
                if (child.ExitCode == 0)
                {
                    if (!File.Exists(resultPath))
                    {
                        throw new InvalidOperationException(
                            $"Publication child succeeded without result artifact for run '{trial.RunId}'.");
                    }

                    caseResult = ReadAndValidateExistingCaseResult(trial, resultPath);
                    resultHash = Sha256(File.ReadAllBytes(resultPath));
                }

                executions.Add(new PilotATrialExecution(
                    trial.RunId,
                    trial.Tier,
                    trial.TrialOrder,
                    trial.CaseId,
                    trial.Seed,
                    trial.ProcessRepetition,
                    child.ExitCode,
                    resultHash,
                    caseResult?.MeasuredReclamationRatio,
                    caseResult?.MeasuredReleasedPayloadBytes,
                    caseResult?.VerifiedProjectionMilliseconds,
                    child.StandardOutput,
                    child.StandardError));

                if (child.ExitCode != 0)
                {
                    break;
                }
            }

            var summaries = executions
                .Where(item => item.ExitCode == 0 && item.MeasuredReclamationRatio.HasValue)
                .GroupBy(item => item.CaseId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new PilotACaseSummary(
                    group.Key,
                    group.Count(),
                    Median(group.Select(item => item.MeasuredReclamationRatio!.Value)),
                    Median(group.Select(item => item.VerifiedProjectionMilliseconds!.Value)),
                    group.Min(item => item.MeasuredReclamationRatio!.Value),
                    group.Max(item => item.MeasuredReclamationRatio!.Value)))
                .ToArray();
            var failureCount = executions.Count(item => item.ExitCode != 0);
            var complete = executions.Count == trialPlan.Length && failureCount == 0;
            var result = new PilotAResult(
                Pilot: smoke ? "A1-PILOT-A-SMOKE" : "A1-PILOT-A",
                PublicationPlanSha256: planHash,
                ExecutionPlanSha256: executionArtifact.Sha256,
                IsSmoke: smoke,
                CanonicalSweepRunCount: canonicalExecutionPlan.SweepRunCount,
                CanonicalRepeatedRunCount: canonicalExecutionPlan.RepeatedRunCount,
                PlannedTrialCount: trialPlan.Length,
                ExecutedTrialCount: executions.Count,
                FailureCount: failureCount,
                Complete: complete,
                Cases: summaries,
                Runs: executions);
            WriteCreateNew(
                Path.Combine(outputDirectory, "pilot-a-result.json"),
                JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine);

            Console.WriteLine(
                $"{result.Pilot} {(complete ? "PASS" : "FAIL")} trials={executions.Count}/{trialPlan.Length} " +
                $"failures={failureCount} plan={planHash} execution={executionArtifact.Sha256} output={outputDirectory}");
            if (smoke)
            {
                Console.WriteLine(
                    "A1-PILOT-A-SMOKE is infrastructure evidence only; it executes a fixed subset of the canonical " +
                    "293-run Pilot-A plan and is not publication Pilot-A evidence.");
            }

            return complete ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{(smoke ? "A1-PILOT-A-SMOKE" : "A1-PILOT-A")} FAIL: {exception}");
            return 1;
        }
    }

    private static ShadowRetentionPilotRunSpec[] BuildSmokeTrials(
        ShadowRetentionPilotExecutionPlan executionPlan,
        int pilotSeed)
    {
        var smokeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "pilot-deep-d08-s025",
            "pilot-neg-b08-s001",
            "pilot-wide-b08-s050-t100",
        };
        var trials = executionPlan.Runs
            .Where(item => item.Tier == ShadowRetentionPilotTier.RepeatedSentinel
                && item.Seed == pilotSeed
                && item.ProcessRepetition == 0
                && smokeIds.Contains(item.CaseId))
            .OrderBy(item => item.TrialOrder)
            .ToArray();
        if (trials.Length != smokeIds.Count)
        {
            throw new InvalidOperationException("Canonical Pilot-A plan does not contain the fixed smoke subset.");
        }

        return trials;
    }

    private static PublicationCaseResult ReadAndValidateExistingCaseResult(
        ShadowRetentionPilotRunSpec trial,
        string resultPath)
    {
        var result = JsonSerializer.Deserialize<PublicationCaseResult>(File.ReadAllText(resultPath), ReadOptions)
            ?? throw new InvalidOperationException(
                $"Could not deserialize existing publication-case result for run '{trial.RunId}'.");
        ValidateCaseIdentity(trial, result);
        return result;
    }

    private static void ValidateCaseIdentity(
        ShadowRetentionPilotRunSpec trial,
        PublicationCaseResult result)
    {
        if (!string.Equals(result.Topology, trial.TopologyKind, StringComparison.Ordinal)
            || result.Seed != trial.Seed
            || result.KeyCount != trial.KeyCount
            || result.BranchCountOrDepth != trial.BranchCountOrDepth
            || BitConverter.DoubleToInt64Bits(result.RequestedShadowFraction)
                != BitConverter.DoubleToInt64Bits(trial.ShadowFraction)
            || BitConverter.DoubleToInt64Bits(result.RequestedTombstoneFraction)
                != BitConverter.DoubleToInt64Bits(trial.TombstoneFraction)
            || !result.FlatExactBaselineVerified
            || !result.CandidateSubsetVerified
            || !result.ObserverEquivalenceVerified
            || !result.ObserverMinimalityVerified)
        {
            throw new InvalidOperationException(
                $"Publication-case result identity or correctness gates do not match sealed run '{trial.RunId}'.");
        }
    }

    private static (ShadowRetentionPublicationPlan Plan, string Hash) ReadAndVerifyPlan(string directory)
    {
        var planPath = Path.Combine(directory, ShadowRetentionPublicationPlanWriter.PlanFileName);
        var hashPath = Path.Combine(directory, ShadowRetentionPublicationPlanWriter.PlanHashFileName);
        if (!File.Exists(planPath) || !File.Exists(hashPath))
        {
            throw new InvalidOperationException("A sealed publication plan and SHA-256 sidecar are required.");
        }

        var plan = JsonSerializer.Deserialize<ShadowRetentionPublicationPlan>(File.ReadAllText(planPath), ReadOptions)
            ?? throw new InvalidOperationException("Could not deserialize the sealed publication plan.");
        plan.Validate();
        var computed = plan.ComputeCanonicalSha256();
        var recorded = File.ReadAllText(hashPath).Trim();
        if (!string.Equals(computed, recorded, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Publication-plan SHA-256 does not match its sealed sidecar.");
        }

        return (plan, computed);
    }

    private static ChildResult RunChild(IReadOnlyList<string> arguments)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the current process path.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly))
            {
                throw new InvalidOperationException("Could not resolve the current entry assembly for child execution.");
            }

            start.ArgumentList.Add(entryAssembly);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start independent A1 publication child process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("A1 publication child exceeded the 180 second per-run limit.");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return new ChildResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static ShadowAwareRetentionProjectionResult AnalyzeAndValidate(
        ResearchRetentionSnapshot snapshot,
        ShadowRetentionEffectPrediction expected)
    {
        var result = new ShadowAwareRetentionProjection(snapshot).Analyze();
        if (!result.FlatExactBaselineVerified
            || !result.CandidateIsSubsetOfBaseline
            || !result.ObserverEquivalenceVerified
            || !result.ObserverMinimalityVerified
            || result.ShadowReleasedPayloadBytes != (long)expected.ReleasedParentPayloadBytes
            || Math.Abs(result.ShadowAwareReclamationRatio - expected.ShadowAwareReclamationRatio) > 1e-12)
        {
            throw new InvalidOperationException(
                $"Publication-case invariant failed: release={result.ShadowReleasedPayloadBytes}, " +
                $"expected={expected.ReleasedParentPayloadBytes}, ratio={result.ShadowAwareReclamationRatio:F12}, " +
                $"expectedRatio={expected.ShadowAwareReclamationRatio:F12}, flat={result.FlatExactBaselineVerified}, " +
                $"subset={result.CandidateIsSubsetOfBaseline}, equivalent={result.ObserverEquivalenceVerified}, " +
                $"minimal={result.ObserverMinimalityVerified}.");
        }

        return result;
    }

    private static (ResearchRetentionSnapshot Snapshot, int ShadowKeyCount, int TombstoneKeyCount) BuildWideSnapshot(
        int keyCount,
        int branchCount,
        double shadowFraction,
        double tombstoneFraction,
        int valueBytes,
        int seed)
    {
        var shadowKeyCount = (int)Math.Floor(keyCount * shadowFraction);
        var tombstoneKeyCount = (int)Math.Floor(shadowKeyCount * tombstoneFraction);
        var mainHistoryId = DeterministicGuid(500_001);
        var histories = new List<ResearchHistoryRetentionSnapshot>(branchCount + 1);
        var roots = new List<ResearchPersistentRetentionRootSnapshot>(branchCount);
        var mainVersions = new List<ResearchCommittedVersionSnapshot>((branchCount + 1) * keyCount);
        for (var generation = 1; generation <= branchCount + 1; generation++)
        {
            for (var keyId = 0; keyId < keyCount; keyId++)
            {
                mainVersions.Add(Version(
                    $"pub-main:g{generation}:k{keyId}",
                    510_000 + generation,
                    (ulong)generation,
                    keyId,
                    valueBytes,
                    tombstone: false));
            }
        }

        histories.Add(new ResearchHistoryRetentionSnapshot(
            mainHistoryId,
            (ulong)(branchCount + 1),
            (ulong)(branchCount + 1),
            Array.AsReadOnly(mainVersions.ToArray())));

        for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
        {
            var historyId = DeterministicGuid(checked(520_000 + branchIndex));
            var selected = SelectDeterministicKeys(keyCount, shadowKeyCount, seed, branchIndex);
            var tombstones = SelectDeterministicSubset(selected, tombstoneKeyCount, seed, checked(10_000 + branchIndex));
            var branchVersions = selected
                .Select(keyId => Version(
                    $"pub-b{branchIndex}:k{keyId}",
                    530_000 + branchIndex,
                    1,
                    keyId,
                    valueBytes,
                    tombstones.Contains(keyId)))
                .ToArray();
            histories.Add(new ResearchHistoryRetentionSnapshot(
                historyId,
                1,
                1,
                Array.AsReadOnly(branchVersions)));
            roots.Add(new ResearchPersistentRetentionRootSnapshot(
                DeterministicGuid(checked(540_000 + branchIndex)),
                "BranchBase",
                historyId,
                mainHistoryId,
                (ulong)(branchIndex + 1)));
        }

        return (
            new ResearchRetentionSnapshot(
                Array.AsReadOnly(histories.ToArray()),
                Array.AsReadOnly(roots.ToArray()),
                Array.Empty<ResearchActiveRetentionBoundarySnapshot>()),
            shadowKeyCount,
            tombstoneKeyCount);
    }

    private static (ResearchRetentionSnapshot Snapshot, int ShadowKeyCount) BuildNestedSnapshot(
        int keyCount,
        int depth,
        double shadowFraction,
        int valueBytes,
        int seed)
    {
        var shadowKeyCount = (int)Math.Floor(keyCount * shadowFraction);
        var selectedKeys = SelectDeterministicKeys(keyCount, shadowKeyCount, seed, stream: 0);
        var histories = new List<ResearchHistoryRetentionSnapshot>(depth + 1);
        var roots = new List<ResearchPersistentRetentionRootSnapshot>(depth);
        var mainId = DeterministicGuid(600_001);
        var mainVersions = new List<ResearchCommittedVersionSnapshot>(keyCount + shadowKeyCount);
        for (var keyId = 0; keyId < keyCount; keyId++)
        {
            mainVersions.Add(Version("nested-main:g1:k" + keyId, 610_001, 1, keyId, valueBytes, false));
        }

        foreach (var keyId in selectedKeys)
        {
            mainVersions.Add(Version("nested-main:g2:k" + keyId, 610_002, 2, keyId, valueBytes, false));
        }

        histories.Add(new ResearchHistoryRetentionSnapshot(
            mainId,
            2,
            2,
            Array.AsReadOnly(mainVersions.ToArray())));

        var parentId = mainId;
        for (var level = 0; level < depth; level++)
        {
            var historyId = DeterministicGuid(checked(620_000 + level));
            var hasChild = level + 1 < depth;
            var versions = new List<ResearchCommittedVersionSnapshot>(shadowKeyCount * (hasChild ? 2 : 1));
            foreach (var keyId in selectedKeys)
            {
                versions.Add(Version($"nested-l{level + 1}:g1:k{keyId}", 630_000 + level, 1, keyId, valueBytes, false));
                if (hasChild)
                {
                    versions.Add(Version($"nested-l{level + 1}:g2:k{keyId}", 640_000 + level, 2, keyId, valueBytes, false));
                }
            }

            histories.Add(new ResearchHistoryRetentionSnapshot(
                historyId,
                hasChild ? 2UL : 1UL,
                hasChild ? 2UL : 1UL,
                Array.AsReadOnly(versions.ToArray())));
            roots.Add(new ResearchPersistentRetentionRootSnapshot(
                DeterministicGuid(checked(650_000 + level)),
                "BranchBase",
                historyId,
                parentId,
                1));
            parentId = historyId;
        }

        return (
            new ResearchRetentionSnapshot(
                Array.AsReadOnly(histories.ToArray()),
                Array.AsReadOnly(roots.ToArray()),
                Array.Empty<ResearchActiveRetentionBoundarySnapshot>()),
            shadowKeyCount);
    }

    private static ResearchCommittedVersionSnapshot Version(
        string versionId,
        int transactionSeed,
        ulong sequence,
        int keyId,
        int valueBytes,
        bool tombstone)
        => new(
            versionId,
            DeterministicGuid(transactionSeed),
            sequence,
            $"k{keyId:D8}",
            8,
            tombstone ? 0 : valueBytes,
            tombstone);

    private static int[] SelectDeterministicKeys(int keyCount, int selectedCount, int seed, int stream)
    {
        if (selectedCount <= 0)
        {
            return [];
        }

        if (selectedCount >= keyCount)
        {
            return Enumerable.Range(0, keyCount).ToArray();
        }

        return Enumerable.Range(0, keyCount)
            .Select(keyId => (KeyId: keyId, Score: StableKeyScore(keyId, seed, stream)))
            .OrderBy(item => item.Score)
            .ThenBy(item => item.KeyId)
            .Take(selectedCount)
            .Select(item => item.KeyId)
            .Order()
            .ToArray();
    }

    private static HashSet<int> SelectDeterministicSubset(
        IReadOnlyList<int> source,
        int selectedCount,
        int seed,
        int stream)
    {
        if (selectedCount <= 0)
        {
            return [];
        }

        return source
            .Select(keyId => (KeyId: keyId, Score: StableKeyScore(keyId, seed, stream)))
            .OrderBy(item => item.Score)
            .ThenBy(item => item.KeyId)
            .Take(selectedCount)
            .Select(item => item.KeyId)
            .ToHashSet();
    }

    private static ulong StableKeyScore(int keyId, int seed, int stream)
    {
        unchecked
        {
            var value = ((ulong)(uint)keyId << 32)
                ^ (uint)seed
                ^ (0x9E3779B97F4A7C15UL * (ulong)(uint)(stream + 1));
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    private static Guid DeterministicGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        BitConverter.TryWriteBytes(bytes[4..], unchecked(value * 397));
        BitConverter.TryWriteBytes(bytes[8..], unchecked(value * 7919));
        BitConverter.TryWriteBytes(bytes[12..], unchecked(value * 104729));
        return new Guid(bytes);
    }

    private static void WriteCreateNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0d;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private sealed record ChildResult(int ExitCode, string StandardOutput, string StandardError);
}

internal sealed record PublicationCaseResult(
    string Pilot,
    string Topology,
    int Seed,
    int KeyCount,
    int BranchCountOrDepth,
    double RequestedShadowFraction,
    double RequestedTombstoneFraction,
    int RealizedShadowKeyCount,
    int RealizedTombstoneKeyCount,
    int VersionCount,
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long ExpectedReleasedPayloadBytes,
    long MeasuredReleasedPayloadBytes,
    double ExpectedReclamationRatio,
    double MeasuredReclamationRatio,
    bool FlatExactBaselineVerified,
    bool CandidateSubsetVerified,
    bool ObserverEquivalenceVerified,
    bool ObserverMinimalityVerified,
    int ObserverEquivalenceCheckCount,
    int ObserverKeyResolutionCount,
    int ParentFallbackHops,
    double VerifiedProjectionMilliseconds,
    double ConstructionMilliseconds,
    double CoreProjectionMilliseconds,
    double ObserverVerificationMilliseconds,
    long ThreadAllocatedBytes);

internal sealed record PilotATrialExecution(
    string RunId,
    ShadowRetentionPilotTier Tier,
    int TrialOrder,
    string CaseId,
    int Seed,
    int ProcessRepetition,
    int ExitCode,
    string? ResultSha256,
    double? MeasuredReclamationRatio,
    long? MeasuredReleasedPayloadBytes,
    double? VerifiedProjectionMilliseconds,
    string StandardOutput,
    string StandardError);

internal sealed record PilotACaseSummary(
    string CaseId,
    int RunCount,
    double MedianReclamationRatio,
    double MedianVerifiedProjectionMilliseconds,
    double MinimumReclamationRatio,
    double MaximumReclamationRatio);

internal sealed record PilotAResult(
    string Pilot,
    string PublicationPlanSha256,
    string ExecutionPlanSha256,
    bool IsSmoke,
    int CanonicalSweepRunCount,
    int CanonicalRepeatedRunCount,
    int PlannedTrialCount,
    int ExecutedTrialCount,
    int FailureCount,
    bool Complete,
    IReadOnlyList<PilotACaseSummary> Cases,
    IReadOnlyList<PilotATrialExecution> Runs);
