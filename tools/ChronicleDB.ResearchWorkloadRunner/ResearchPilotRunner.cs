using System.Diagnostics;
using System.Text.Json;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;

internal static class ResearchPilotRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return Task.FromResult(2);
        }

        return args[0].ToUpperInvariant() switch
        {
            "P1" => Task.FromResult(RunRetentionPilot(args[1..])),
            "P1C" => Task.FromResult(RunRetentionControlPilot(args[1..])),
            "P2" => Task.FromResult(RunCrashPorPilot(args[1..])),
            "P2R" => Task.FromResult(RunRealTraceCrashPorPilot(args[1..])),
            "P3" => Task.FromResult(RunAncestryPilot(args[1..])),
            "P3T" => Task.FromResult(RunAncestryTimingControlPilot(args[1..])),
            "P4" => Task.FromResult(RunRecoverySchedulingPilot(args[1..])),
            "P5" => Task.FromResult(RunRecoveryCompositionPilot(args[1..])),
            "P6" => Task.FromResult(RunErasureClosurePilot(args[1..])),
            _ => Task.FromResult(UnknownPilot(args[0])),
        };
    }

    private static int RunAncestryTimingControlPilot(string[] args)
    {
        if (args.Length < 2
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var readsPerDepth)
            || readsPerDepth < 100)
        {
            Console.Error.WriteLine("Usage: pilot P3T <seed> <reads-per-depth>=100+ [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p3t-{seed}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var databaseDirectory = Path.Combine(outputDirectory, "database");

        try
        {
            // Deliberately keep research telemetry disabled in this pass. P3 uses a
            // separate metrics pass for exact ancestor-probe counts; mixing event
            // publication into the latency path materially distorts the depth signal.
            using var database = ChronicleDatabase.Open(databaseDirectory);
            var inheritedKey = Key(0);
            database.Put(inheritedKey, Payload(64, new Random(seed), salt: seed));

            var branches = new Dictionary<int, ChronicleBranch>();
            ChronicleBranch? parent = null;
            for (var depth = 1; depth <= 16; depth++)
            {
                var current = parent is null
                    ? database.CreateBranch($"p3t-depth-{depth}")
                    : parent.CreateBranch($"p3t-depth-{depth}");
                branches.Add(depth, current);
                parent = current;
                current.Put(Key(30_000 + depth), Payload(64, new Random(seed + depth), salt: depth));
            }

            try
            {
                var depths = new[] { 1, 2, 4, 8, 16 };
                var order = depths.OrderBy(_ => new Random(seed + _).Next()).ToArray();
                var results = new List<AncestryTimingDepthPilotResult>();
                foreach (var depth in order)
                {
                    var branch = branches[depth];
                    for (var warmup = 0; warmup < 500; warmup++)
                    {
                        _ = branch.TryGet(inheritedKey, out _);
                    }

                    var inherited = MeasureReads(branch, inheritedKey, readsPerDepth, expectedFound: true);
                    var local = MeasureReads(
                        branch,
                        Key(30_000 + depth),
                        Math.Max(100, readsPerDepth / 4),
                        expectedFound: true);
                    var missing = MeasureReads(
                        branch,
                        Key(40_000 + depth),
                        Math.Max(100, readsPerDepth / 4),
                        expectedFound: false);
                    results.Add(new AncestryTimingDepthPilotResult(
                        depth,
                        inherited.P50Nanoseconds,
                        inherited.P95Nanoseconds,
                        inherited.P99Nanoseconds,
                        inherited.MeanNanoseconds,
                        local.P99Nanoseconds,
                        missing.P99Nanoseconds));
                }

                var ordered = results.OrderBy(result => result.Depth).ToArray();
                var depth1 = ordered.Single(result => result.Depth == 1);
                var depth16 = ordered.Single(result => result.Depth == 16);
                var result = new AncestryTimingControlPilotResult(
                    Pilot: "P3T",
                    Seed: seed,
                    ReadsPerDepth: readsPerDepth,
                    Depths: ordered,
                    InheritedP50Amplification: Ratio(depth16.InheritedP50Nanoseconds, depth1.InheritedP50Nanoseconds),
                    InheritedP95Amplification: Ratio(depth16.InheritedP95Nanoseconds, depth1.InheritedP95Nanoseconds),
                    InheritedP99Amplification: Ratio(depth16.InheritedP99Nanoseconds, depth1.InheritedP99Nanoseconds),
                    MissingP99Amplification: Ratio(depth16.MissingP99Nanoseconds, depth1.MissingP99Nanoseconds),
                    TelemetryDisabled: database.GetResearchTelemetryStatus().Mode == ResearchTelemetryMode.Disabled);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "p3t-result.json"),
                    JsonSerializer.Serialize(result, JsonOptions));
                Console.WriteLine(
                    $"P3T PASS p50={result.InheritedP50Amplification:F2} p95={result.InheritedP95Amplification:F2} " +
                    $"p99={result.InheritedP99Amplification:F2} missing-p99={result.MissingP99Amplification:F2} " +
                    $"output={outputDirectory}");
                return result.TelemetryDisabled ? 0 : 1;
            }
            finally
            {
                foreach (var branch in branches.Values.Reverse())
                {
                    branch.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P3T FAIL: {exception}");
            return 1;
        }
    }

    private static int RunAncestryPilot(string[] args)
    {
        if (args.Length < 2
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var readsPerDepth)
            || readsPerDepth < 10)
        {
            Console.Error.WriteLine("Usage: pilot P3 <seed> <reads-per-depth>=10+ [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p3-{seed}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var databaseDirectory = Path.Combine(outputDirectory, "database");

        try
        {
            var sink = new AncestryMetricsResearchEventSink();
            using var database = ChronicleDatabase.Open(databaseDirectory, researchEventSink: sink);
            var inheritedKey = Key(0);
            database.Put(inheritedKey, Payload(64, new Random(seed), salt: seed));

            var branches = new Dictionary<int, ChronicleBranch>();
            ChronicleBranch? parent = null;
            for (var depth = 1; depth <= 16; depth++)
            {
                var current = parent is null
                    ? database.CreateBranch($"p3-depth-{depth}")
                    : parent.CreateBranch($"p3-depth-{depth}");
                branches.Add(depth, current);
                parent = current;
            }

            try
            {
                var results = new List<AncestryDepthPilotResult>();
                foreach (var depth in new[] { 1, 2, 4, 8, 16 })
                {
                    var branch = branches[depth];
                    var localKey = Key(10_000 + depth);
                    branch.Put(localKey, Payload(64, new Random(seed + depth), salt: depth));

                    // First read approximates a cold logical lookup. The measured warm
                    // loop remains on the exact v1.0 recursive path; no routing cache is
                    // installed by this pilot.
                    var coldNanoseconds = MeasureSingleRead(branch, inheritedKey, expectedFound: true);
                    for (var warmup = 0; warmup < Math.Min(100, readsPerDepth / 10); warmup++)
                    {
                        _ = branch.TryGet(inheritedKey, out _);
                    }

                    var inherited = MeasureReads(branch, inheritedKey, readsPerDepth, expectedFound: true);
                    var local = MeasureReads(branch, localKey, Math.Max(10, readsPerDepth / 4), expectedFound: true);
                    var missing = MeasureReads(branch, Key(20_000 + depth), Math.Max(10, readsPerDepth / 4), expectedFound: false);
                    results.Add(new AncestryDepthPilotResult(
                        depth,
                        coldNanoseconds,
                        inherited.P50Nanoseconds,
                        inherited.P95Nanoseconds,
                        inherited.P99Nanoseconds,
                        inherited.MeanNanoseconds,
                        local.P99Nanoseconds,
                        missing.P99Nanoseconds,
                        ExpectedAncestorProbes: depth));
                }

                var metrics = sink.Snapshot();
                var depth1 = results.Single(result => result.Depth == 1);
                var depth16 = results.Single(result => result.Depth == 16);
                var apa = depth1.InheritedP99Nanoseconds <= 0
                    ? 0d
                    : depth16.InheritedP99Nanoseconds / depth1.InheritedP99Nanoseconds;
                var repeatedLookupOpportunity = readsPerDepth <= 1
                    ? 0d
                    : (double)(readsPerDepth - 1) / readsPerDepth;
                var result = new AncestryPilotResult(
                    Pilot: "P3-A",
                    Seed: seed,
                    ReadsPerDepth: readsPerDepth,
                    Depths: results,
                    AncestryP99Amplification: apa,
                    ReadCount: metrics.ReadCount,
                    LocalReadCount: metrics.LocalReadCount,
                    InheritedReadCount: metrics.InheritedReadCount,
                    MissingReadCount: metrics.MissingReadCount,
                    LocalMissCount: metrics.LocalMissCount,
                    TombstoneShadowCount: metrics.TombstoneShadowCount,
                    AncestorProbeCount: metrics.AncestorProbeCount,
                    MaximumResolvedAncestorDepth: metrics.MaximumResolvedAncestorDepth,
                    P99ResolvedAncestorDepth: metrics.PercentileResolvedAncestorDepth(0.99),
                    RepeatedLookupOpportunity: repeatedLookupOpportunity,
                    TelemetryComplete: database.GetResearchTelemetryStatus().IsComplete);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "p3-result.json"),
                    JsonSerializer.Serialize(result, JsonOptions));
                Console.WriteLine(
                    $"P3 PASS apa={result.AncestryP99Amplification:F2} reads={result.ReadCount} " +
                    $"probes={result.AncestorProbeCount} max-depth={result.MaximumResolvedAncestorDepth} " +
                    $"output={outputDirectory}");
                return result.TelemetryComplete ? 0 : 1;
            }
            finally
            {
                foreach (var branch in branches.Values.Reverse())
                {
                    branch.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P3 FAIL: {exception}");
            return 1;
        }
    }




    private static int RunErasureClosurePilot(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var seed))
        {
            Console.Error.WriteLine("Usage: pilot P6 <seed> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p6-{seed}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var databaseDirectory = Path.Combine(outputDirectory, "database");

        try
        {
            var random = new Random(seed);
            var key = Key(60_000 + seed);
            using var database = ChronicleDatabase.Open(databaseDirectory);
            database.Put(key, Payload(256, random, 1));
            using var snapshot = database.CreateSnapshot("p6-main-v1");
            using var branchA = database.CreateBranch("p6-a");
            using var nestedA1 = branchA.CreateBranch("p6-a1");
            using var branchB = database.CreateBranch("p6-b");
            branchB.Put(key, Payload(256, random, 2));
            using var branchC = database.CreateBranch("p6-c");
            _ = branchC.Delete(key);

            // Publish history checkpoints while observer contracts still retain v1.
            _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });

            // Re-introduce target-key WAL representations after the checkpoint/WAL
            // authority transition, then leave main current-state tombstoned.
            database.Put(key, Payload(256, random, 3));
            _ = database.Delete(key);
            branchB.Put(key, Payload(256, random, 4));

            var input = database.CaptureResearchErasureClosureInput(key);
            var analysis = ErasureClosureAnalyzer.Analyze(input, ErasureScope.Global);
            var request = ErasureContractEvaluator.Evaluate(input, ErasureScope.Global, ErasureMode.Request);
            var forcePhysical = ErasureContractEvaluator.Evaluate(
                input,
                ErasureScope.Global,
                ErasureMode.Force,
                forceAuthorized: true);
            var logicalOnlyInput = input with
            {
                PhysicalRepresentationScanComplete = true,
                UnscannedPhysicalRepresentations = [],
            };
            var forceLogical = ErasureContractEvaluator.Evaluate(
                logicalOnlyInput,
                ErasureScope.Global,
                ErasureMode.Force,
                forceAuthorized: true);
            var mainCurrentTombstoned = input.Representations.Any(item =>
                item.Kind == ErasureRepresentationKind.DerivedCurrentState
                && item.OwnerHistoryId == input.OriginHistoryId
                && item.Content == ErasureContentState.Tombstone);
            var result = new ErasurePilotResult(
                Pilot: "P6",
                Seed: seed,
                ObserverHistoryCount: analysis.ObserverHistoriesInScope.Count,
                ReachableValueRepresentationCount: analysis.ReachableValueRepresentations.Count,
                BlockingObserverContractCount: analysis.BlockingObserverContracts.Count,
                MvccVersionOccurrences: analysis.MvccVersionOccurrences,
                SnapshotRootOccurrences: analysis.SnapshotRootOccurrences,
                BranchBaseOccurrences: analysis.BranchBaseOccurrences,
                WalOccurrences: analysis.WalOccurrences,
                CheckpointOccurrences: analysis.CheckpointOccurrences,
                DerivedStateOccurrences: analysis.DerivedStateOccurrences,
                MainCurrentStateIsTombstoned: mainCurrentTombstoned,
                RequestOutcome: request.Outcome.ToString(),
                AuthorizedForcePhysicalOutcome: forcePhysical.Outcome.ToString(),
                AuthorizedForceLogicalOnlyOutcome: forceLogical.Outcome.ToString(),
                RequiredLogicalRevocations: forceLogical.RequiredRevocations.Count,
                ProposedLogicalRewriteCount: forceLogical.ProposedRewritePlan.Count,
                PhysicalClosureComplete: analysis.ClosureIsComplete,
                UnscannedPhysicalRepresentations: input.UnscannedPhysicalRepresentations);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p6-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));

            var pass = mainCurrentTombstoned
                && analysis.BlockingObserverContracts.Count > 0
                && analysis.WalOccurrences > 0
                && analysis.CheckpointOccurrences > 0
                && request.Outcome == ErasureContractOutcome.BlockedByObserverContract
                && forcePhysical.Outcome == ErasureContractOutcome.BlockedByIncompleteClosure
                && forceLogical.Outcome == ErasureContractOutcome.ForcePlanReady;
            Console.WriteLine(
                $"P6 {(pass ? "PASS" : "FAIL")} blockers={result.BlockingObserverContractCount} " +
                $"values={result.ReachableValueRepresentationCount} wal={result.WalOccurrences} " +
                $"checkpoint={result.CheckpointOccurrences} physical-complete={result.PhysicalClosureComplete} " +
                $"output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P6 FAIL: {exception}");
            return 1;
        }
    }

    private static int RunRecoveryCompositionPilot(string[] args)
    {
        if (args.Length < 1
            || !int.TryParse(args[0], out var maxDepth)
            || maxDepth is < 1 or > 12)
        {
            Console.Error.WriteLine("Usage: pilot P5 <max-depth:1..12> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p5-{maxDepth}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var safe = RecoveryCompositionModel.Explore(maxDepth);
            var mutants = Enum.GetValues<RecoveryCompositionMutant>()
                .Where(mutant => mutant != RecoveryCompositionMutant.None)
                .Select(mutant => new RecoveryCompositionMutantPilotResult(
                    mutant.ToString(),
                    RecoveryCompositionModel.Explore(maxDepth, mutant)))
                .ToArray();
            stopwatch.Stop();
            var result = new RecoveryCompositionPilotResult(
                Pilot: "P5",
                Model: "Executable bounded Main-to-A composition model; not a mechanized proof",
                MaxDepth: maxDepth,
                UniqueStateCount: safe.UniqueStateCount,
                TransitionCount: safe.TransitionCount,
                SafeModelViolationCount: safe.Violations.Count,
                MutantCount: mutants.Length,
                MutantsRejected: mutants.Count(item => !item.Result.IsSafe),
                Mutants: mutants.Select(item => new RecoveryCompositionMutantSummary(
                    item.Name,
                    item.Result.IsSafe,
                    item.Result.Violations.Select(violation => violation.Invariant).Distinct().Order().ToArray())).ToArray(),
                ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p5-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));
            var pass = safe.IsSafe && result.MutantsRejected == result.MutantCount;
            Console.WriteLine(
                $"P5 {(pass ? "PASS" : "FAIL")} states={result.UniqueStateCount} transitions={result.TransitionCount} " +
                $"safe-violations={result.SafeModelViolationCount} mutants={result.MutantsRejected}/{result.MutantCount} " +
                $"output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P5 FAIL: {exception}");
            return 1;
        }
    }

    private static int RunRecoverySchedulingPilot(string[] args)
    {
        if (args.Length < 3
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var historyCount)
            || !int.TryParse(args[2], out var requestedIndex)
            || historyCount is < 4 or > 64
            || requestedIndex <= 0
            || requestedIndex >= historyCount)
        {
            Console.Error.WriteLine("Usage: pilot P4 <seed> <history-count:4..64> <requested-index:1..count-1> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 4
            ? Path.GetFullPath(args[3])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p4-{seed}-{historyCount}-{requestedIndex}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var random = new Random(seed);
            var ids = Enumerable.Range(0, historyCount)
                .Select(index => DeterministicGuid(seed, index + 1))
                .ToArray();
            var profiles = new List<RecoveryHistoryWorkProfile>(historyCount);
            for (var index = 0; index < historyCount; index++)
            {
                Guid? parent = null;
                var depth = 0;
                if (index > 0)
                {
                    var parentIndex = index >= 4 && index % 4 == 0 ? index - 1 : 0;
                    parent = ids[parentIndex];
                    depth = parentIndex == 0 ? 1 : 2;
                }

                var checkpoint = index == 0
                    ? 256L * 1024
                    : random.NextInt64(32L * 1024, 512L * 1024);
                var wal = index == 0
                    ? 512L * 1024
                    : random.NextInt64(64L * 1024, 4L * 1024 * 1024);
                profiles.Add(new RecoveryHistoryWorkProfile(
                    ids[index],
                    parent,
                    BaselineOrder: index,
                    MetadataValidationWork: 4L * 1024,
                    DependencyValidationWork: (depth + 1L) * 1024,
                    AuthorityValidationWork: 4L * 1024,
                    CheckpointLoadWork: checkpoint,
                    WalReplayWork: wal));
            }

            var requested = ids[requestedIndex];
            var planner = new RecoveryReadinessPlanner(profiles);
            var baseline = planner.Plan(requested, RecoverySchedulingStrategy.RecoverAll);
            var safe = planner.Plan(requested, RecoverySchedulingStrategy.ValidateAllRequestedReplayFirst);
            var aggressive = planner.Plan(requested, RecoverySchedulingStrategy.RequestedDependencyClosureFirst);
            var result = new RecoverySchedulingPilotResult(
                Pilot: "P4",
                Seed: seed,
                HistoryCount: historyCount,
                RequestedIndex: requestedIndex,
                RequestedHistoryId: requested,
                DependencyClosureCount: safe.RequestedDependencyClosure.Count,
                BaselineWorkToRequestedReady: baseline.WorkToRequestedReady,
                SafeRequestedFirstWorkToRequestedReady: safe.WorkToRequestedReady,
                TotalWork: baseline.TotalWork,
                SafeRequestedReadinessSpeedup: safe.RequestedReadinessSpeedupAgainst(baseline),
                SafePlanPreservesGlobalFailClosedSemantics: safe.PreservesGlobalFailClosedSemantics,
                AggressiveWorkToRequestedReady: aggressive.WorkToRequestedReady,
                AggressivePlanPreservesGlobalFailClosedSemantics: aggressive.PreservesGlobalFailClosedSemantics);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p4-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));
            Console.WriteLine(
                $"P4 PASS safe-rs={result.SafeRequestedReadinessSpeedup:F2} histories={historyCount} " +
                $"closure={result.DependencyClosureCount} fail-closed={result.SafePlanPreservesGlobalFailClosedSemantics} " +
                $"output={outputDirectory}");
            return result.SafePlanPreservesGlobalFailClosedSemantics ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P4 FAIL: {exception}");
            return 1;
        }
    }

    private static int RunRealTraceCrashPorPilot(string[] args)
    {
        if (args.Length < 1
            || !int.TryParse(args[0], out var historyCount)
            || historyCount is < 2 or > 3)
        {
            Console.Error.WriteLine("Usage: pilot P2R <history-count:2..3> [siblings|chain] [output-directory]");
            return 2;
        }

        var topology = args.Length >= 2
            && (args[1].Equals("siblings", StringComparison.OrdinalIgnoreCase)
                || args[1].Equals("chain", StringComparison.OrdinalIgnoreCase))
            ? args[1].ToLowerInvariant()
            : "siblings";
        var outputArgumentIndex = topology == "siblings" && args.Length >= 2
            && !args[1].Equals("siblings", StringComparison.OrdinalIgnoreCase)
            && !args[1].Equals("chain", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;
        var outputDirectory = args.Length > outputArgumentIndex
            ? Path.GetFullPath(args[outputArgumentIndex])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p2r-{topology}-{historyCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var databaseDirectory = Path.Combine(outputDirectory, "database");

        try
        {
            var sink = new TraceResearchEventSink();
            using var database = ChronicleDatabase.Open(databaseDirectory, researchEventSink: sink);
            var branches = new List<ChronicleBranch>(historyCount);
            try
            {
                for (var index = 0; index < historyCount; index++)
                {
                    var branch = topology == "chain" && index > 0
                        ? branches[index - 1].CreateBranch($"p2r-chain-{index}")
                        : database.CreateBranch($"p2r-{topology}-{index}");
                    branches.Add(branch);
                }

                var watermark = sink.LastLogicalEventId;
                for (var index = 0; index < branches.Count; index++)
                {
                    branches[index].Put(Key(50_000 + index), BitConverter.GetBytes(index + 1));
                }

                var telemetry = database.GetResearchTelemetryStatus();
                if (!telemetry.IsComplete)
                {
                    throw new InvalidOperationException(
                        $"P2R requires a complete trace; publicationFailures={telemetry.PublicationFailures}.");
                }

                var productionEvents = sink.Snapshot()
                    .Where(researchEvent => researchEvent.LogicalEventId > watermark)
                    .ToArray();
                ResearchTraceValidator.Validate(productionEvents);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "p2r-production-trace.json"),
                    ResearchTraceSerializer.SerializeCanonical(productionEvents));

                var actions = PersistenceTraceSlice.SelectCompleteOperations(productionEvents, historyCount);
                var oracle = new PersistenceTraceProjectionOracle(actions);
                var explorer = new BoundedCrashPorExplorer(actions, maximumActions: historyCount * 4);
                var stopwatch = Stopwatch.StartNew();
                var verification = explorer.VerifyCrashPrefixEquivalence(oracle.Evaluate);

                // Strong generic baseline: resource/dependency-aware POR without any
                // ChronicleDB branch ancestry model. If this matches the proposed
                // reducer everywhere, the history-domain contribution is weaker.
                var proposedIndependence = new ConservativeHistoryIndependence(actions);
                var resourceBaselineIndependence = new ResourceDependencyIndependence();
                var resourceBaselineUsesSameRelation = HaveSameIndependenceRelation(
                    actions,
                    proposedIndependence,
                    resourceBaselineIndependence);
                var resourceBaselineVerification = resourceBaselineUsesSameRelation
                    ? verification
                    : new BoundedCrashPorExplorer(
                        actions,
                        maximumActions: historyCount * 4,
                        independence: resourceBaselineIndependence)
                        .VerifyCrashPrefixEquivalence(oracle.Evaluate);

                // Same-budget random crash sampling. This does not claim soundness; it
                // attacks whether deterministic reduction buys semantic coverage beyond
                // a simple seeded campaign with the same number of executed plans.
                var randomBaseline = explorer.SampleRandomCrashPlans(
                    oracle.Evaluate,
                    sampleBudget: Math.Max(1, verification.ReducedCrashPlanCount),
                    seed: 20_260_811 + historyCount);

                // Negative control: deliberately hide the shared branch-catalog touch
                // from the independence relation while keeping the real observer. If
                // equivalence survives this control, the claimed resource-aware POR
                // distinction is not doing useful work.
                var catalogBlindActions = actions
                    .Select(action => new PersistenceAction(
                        action.ActionId,
                        action.EventKind,
                        action.HistoryId,
                        action.ParentHistoryId,
                        action.OperationId,
                        action.ResourceSet.Where(resource => !resource.Equals("branch-catalog", StringComparison.Ordinal)),
                        action.DurabilityPhase,
                        action.AuthorityGeneration,
                        action.DependencyActionIds))
                    .ToArray();
                var realActionById = actions.ToDictionary(action => action.ActionId);
                var catalogBlindExplorer = new BoundedCrashPorExplorer(
                    catalogBlindActions,
                    maximumActions: historyCount * 4);
                var catalogBlindVerification = catalogBlindExplorer.VerifyCrashPrefixEquivalence(
                    prefix => oracle.Evaluate(prefix.Select(action => realActionById[action.ActionId]).ToArray()));
                stopwatch.Stop();

                var sharedCatalogActions = actions.Count(action => action.ResourceSet.Contains("branch-catalog", StringComparer.Ordinal));
                var result = new RealTraceCrashPorPilotResult(
                    Pilot: "P2R",
                    Topology: topology,
                    HistoryCount: historyCount,
                    ActionCount: actions.Count,
                    ProductionTraceEventCount: productionEvents.Length,
                    SharedResources: oracle.SharedResources.Order(StringComparer.Ordinal).ToArray(),
                    SharedCatalogActionCount: sharedCatalogActions,
                    ExhaustiveOrderCount: verification.ExhaustiveOrderCount,
                    ReducedOrderCount: verification.ReducedOrderCount,
                    ExhaustiveCrashPlanCount: verification.ExhaustiveCrashPlanCount,
                    ReducedCrashPlanCount: verification.ReducedCrashPlanCount,
                    ExhaustiveObservationTraceCount: verification.ExhaustiveObservationTraceCount,
                    ReducedObservationTraceCount: verification.ReducedObservationTraceCount,
                    ObservationSetsEquivalent: verification.ObservationSetsEquivalent,
                    OrderReductionFactor: verification.OrderReductionFactor,
                    CrashPlanReductionFactor: verification.CrashPlanReductionFactor,
                    ResourceBaselineUsesSameIndependenceRelation: resourceBaselineUsesSameRelation,
                    ResourceBaselineObservationSetsEquivalent: resourceBaselineVerification.ObservationSetsEquivalent,
                    ResourceBaselineReducedCrashPlanCount: resourceBaselineVerification.ReducedCrashPlanCount,
                    ResourceBaselineCrashPlanReductionFactor: resourceBaselineVerification.CrashPlanReductionFactor,
                    RandomBaselineSampleBudget: randomBaseline.SampleBudget,
                    RandomBaselineUniqueCrashPlans: randomBaseline.UniqueCrashPlansSampled,
                    RandomBaselineObservationTraceCount: randomBaseline.UniqueObservationTraceCount,
                    RandomBaselineObservationCoverage: randomBaseline.ObservationCoverage(verification.ExhaustiveObservationTraceCount),
                    CatalogBlindObservationSetsEquivalent: catalogBlindVerification.ObservationSetsEquivalent,
                    CatalogBlindReducedCrashPlanCount: catalogBlindVerification.ReducedCrashPlanCount,
                    CatalogBlindCrashPlanReductionFactor: catalogBlindVerification.CrashPlanReductionFactor,
                    ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "p2r-result.json"),
                    JsonSerializer.Serialize(result, JsonOptions));
                Console.WriteLine(
                    $"P2R {(result.ObservationSetsEquivalent ? "PASS" : "FAIL")} topology={topology} histories={historyCount} " +
                    $"actions={result.ActionCount} shared={string.Join(',', result.SharedResources)} " +
                    $"exhaustive-plans={result.ExhaustiveCrashPlanCount} reduced-plans={result.ReducedCrashPlanCount} " +
                    $"crf={result.CrashPlanReductionFactor:F2} generic-crf={result.ResourceBaselineCrashPlanReductionFactor:F2} " +
                    $"random-coverage={result.RandomBaselineObservationCoverage:P1} " +
                    $"catalog-blind-eq={result.CatalogBlindObservationSetsEquivalent} output={outputDirectory}");
                return result.ObservationSetsEquivalent && !result.CatalogBlindObservationSetsEquivalent ? 0 : 1;
            }
            finally
            {
                foreach (var branch in branches.AsEnumerable().Reverse())
                {
                    branch.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P2R FAIL: {exception}");
            return 1;
        }
    }

    private static bool HaveSameIndependenceRelation(
        IReadOnlyList<PersistenceAction> actions,
        ConservativeHistoryIndependence left,
        ResourceDependencyIndependence right)
    {
        for (var first = 0; first < actions.Count; first++)
        {
            for (var second = first + 1; second < actions.Count; second++)
            {
                if (left.AreIndependent(actions[first], actions[second])
                    != right.AreIndependent(actions[first], actions[second]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int RunCrashPorPilot(string[] args)
    {
        if (args.Length < 1
            || !int.TryParse(args[0], out var historyCount)
            || historyCount is < 2 or > 4)
        {
            Console.Error.WriteLine("Usage: pilot P2 <history-count:2..4> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p2-{historyCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var actions = new List<PersistenceAction>(historyCount * 2);
            long actionId = 1;
            for (var index = 0; index < historyCount; index++)
            {
                var history = new ChronicleDB.Core.Identifiers.HistoryId(
                    Guid.Parse($"{index + 1:00000000}-0000-0000-0000-000000000001"));
                var operation = Guid.Parse($"{index + 1:00000000}-0000-0000-0000-0000000000aa");
                var barrierId = actionId++;
                actions.Add(new PersistenceAction(
                    barrierId,
                    ResearchEventKind.DurabilityBarrier,
                    history,
                    null,
                    operation,
                    [$"history-{index}.wal", $"history-{index}.checkpoint"],
                    ResearchDurabilityPhase.StableStorageBarrier,
                    authorityGeneration: 1));
                actions.Add(new PersistenceAction(
                    actionId++,
                    ResearchEventKind.AuthorityPublished,
                    history,
                    null,
                    operation,
                    [$"history-{index}.wal", $"history-{index}.checkpoint"],
                    ResearchDurabilityPhase.AuthorityPublished,
                    authorityGeneration: 1,
                    [barrierId]));
            }

            var stopwatch = Stopwatch.StartNew();
            var explorer = new BoundedCrashPorExplorer(actions);
            var verification = explorer.VerifyCrashPrefixEquivalence(
                prefix => PersistenceProtocolOracle.Evaluate(prefix).Trace);
            stopwatch.Stop();
            var mutants = PersistenceMutationCorpus.Evaluate();
            var result = new CrashPorPilotResult(
                Pilot: "P2",
                HistoryCount: historyCount,
                ActionCount: actions.Count,
                ExhaustiveOrderCount: verification.ExhaustiveOrderCount,
                ReducedOrderCount: verification.ReducedOrderCount,
                ExhaustiveCrashPlanCount: verification.ExhaustiveCrashPlanCount,
                ReducedCrashPlanCount: verification.ReducedCrashPlanCount,
                ExhaustiveObservationTraceCount: verification.ExhaustiveObservationTraceCount,
                ReducedObservationTraceCount: verification.ReducedObservationTraceCount,
                ObservationSetsEquivalent: verification.ObservationSetsEquivalent,
                OrderReductionFactor: verification.OrderReductionFactor,
                CrashPlanReductionFactor: verification.CrashPlanReductionFactor,
                MutantsKilled: mutants.Count(mutant => mutant.Killed),
                MutantCount: mutants.Count,
                Mutants: mutants,
                ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p2-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));
            Console.WriteLine(
                $"P2 {(result.ObservationSetsEquivalent && result.MutantsKilled == result.MutantCount ? "PASS" : "FAIL")} " +
                $"histories={historyCount} exhaustive-plans={result.ExhaustiveCrashPlanCount} " +
                $"reduced-plans={result.ReducedCrashPlanCount} crf={result.CrashPlanReductionFactor:F2} " +
                $"mutants={result.MutantsKilled}/{result.MutantCount} output={outputDirectory}");
            return result.ObservationSetsEquivalent && result.MutantsKilled == result.MutantCount ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P2 FAIL: {exception}");
            return 1;
        }
    }

    private static int RunRetentionControlPilot(string[] args)
    {
        if (args.Length < 3
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var baseKeyCount)
            || !int.TryParse(args[2], out var valueBytes)
            || baseKeyCount <= 0
            || valueBytes <= 0)
        {
            Console.Error.WriteLine(
                "Usage: pilot P1C <seed> <base-key-count> <value-bytes> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 4
            ? Path.GetFullPath(args[3])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p1c-{seed}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var databaseDirectory = Path.Combine(outputDirectory, "database");

        try
        {
            var random = new Random(seed);
            using var database = ChronicleDatabase.Open(databaseDirectory);
            for (var keyId = 0; keyId < baseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(valueBytes, random, salt: keyId));
            }

            using var branchA = database.CreateBranch("p1-control-a");
            using var branchB = database.CreateBranch("p1-control-b");
            branchA.Put(Key(baseKeyCount + 1), Payload(Math.Min(valueBytes, 4096), random, salt: 0xA1));
            branchB.Put(Key(baseKeyCount + 2), Payload(Math.Min(valueBytes, 4096), random, salt: 0xB1));

            _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            var noChurnSnapshot = database.CaptureResearchRetentionSnapshot();
            var noChurnInspector = new RetentionInspector(noChurnSnapshot);
            var noChurnRoots = FindBranchBaseRoots(database, branchA.HistoryId, branchB.HistoryId);
            var noChurnDropA = noChurnInspector.WhatIfDrop(noChurnRoots.RootA);
            var noChurnDropBoth = noChurnInspector.WhatIfDrop([noChurnRoots.RootA, noChurnRoots.RootB]);

            for (var keyId = 0; keyId < baseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(valueBytes, random, salt: keyId + 20_000));
            }

            _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            var churnSnapshot = database.CaptureResearchRetentionSnapshot();
            var churnInspector = new RetentionInspector(churnSnapshot);
            var churnRoots = FindBranchBaseRoots(database, branchA.HistoryId, branchB.HistoryId);
            var dropA = churnInspector.WhatIfDrop(churnRoots.RootA);
            var dropB = churnInspector.WhatIfDrop(churnRoots.RootB);
            var dropBoth = churnInspector.WhatIfDrop([churnRoots.RootA, churnRoots.RootB]);

            var expectedBasePayload = checked((long)baseKeyCount * valueBytes);
            var result = new RetentionControlPilotResult(
                Pilot: "P1C",
                Seed: seed,
                BaseKeyCount: baseKeyCount,
                ValueBytes: valueBytes,
                ExpectedBasePayloadBytes: expectedBasePayload,
                NoChurnDropASingleMarginalBytes: noChurnDropA.MarginalPayloadBytes,
                NoChurnDropBothMarginalBytes: noChurnDropBoth.MarginalPayloadBytes,
                ChurnDropASingleMarginalBytes: dropA.MarginalPayloadBytes,
                ChurnDropBSingleMarginalBytes: dropB.MarginalPayloadBytes,
                ChurnDropBothMarginalBytes: dropBoth.MarginalPayloadBytes,
                NonAdditiveOverlapObserved: dropA.MarginalPayloadBytes == 0
                    && dropB.MarginalPayloadBytes == 0
                    && dropBoth.MarginalPayloadBytes > 0,
                NoChurnNullControlPassed: noChurnDropA.MarginalPayloadBytes == 0
                    && noChurnDropBoth.MarginalPayloadBytes == 0);
            File.WriteAllText(
                Path.Combine(outputDirectory, "p1c-result.json"),
                JsonSerializer.Serialize(result, JsonOptions));
            Console.WriteLine(
                $"P1C {(result.NonAdditiveOverlapObserved && result.NoChurnNullControlPassed ? "PASS" : "FAIL")} " +
                $"no-churn={result.NoChurnDropBothMarginalBytes} singleA={result.ChurnDropASingleMarginalBytes} " +
                $"singleB={result.ChurnDropBSingleMarginalBytes} pair={result.ChurnDropBothMarginalBytes} " +
                $"output={outputDirectory}");
            return result.NonAdditiveOverlapObserved && result.NoChurnNullControlPassed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1C FAIL: {exception}");
            return 1;
        }
    }

    private static (Guid RootA, Guid RootB) FindBranchBaseRoots(
        ChronicleDatabase database,
        Guid historyA,
        Guid historyB)
    {
        var roots = database.GetHistoryTopologyDiagnostics().RetentionRoots;
        var rootA = roots.Single(root =>
            root.Kind.Equals("BranchBase", StringComparison.Ordinal)
            && root.OwnerHistoryId == historyA);
        var rootB = roots.Single(root =>
            root.Kind.Equals("BranchBase", StringComparison.Ordinal)
            && root.OwnerHistoryId == historyB);
        return (rootA.RootId, rootB.RootId);
    }

    private static int RunRetentionPilot(string[] args)
    {
        if (args.Length < 4
            || !int.TryParse(args[0], out var seed)
            || !int.TryParse(args[1], out var baseKeyCount)
            || !int.TryParse(args[2], out var valueBytes)
            || !int.TryParse(args[3], out var privateBytes)
            || baseKeyCount <= 0
            || valueBytes <= 0
            || privateBytes <= 0)
        {
            Console.Error.WriteLine(
                "Usage: pilot P1 <seed> <base-key-count> <value-bytes> <private-bytes> [output-directory]");
            return 2;
        }

        var outputDirectory = args.Length >= 5
            ? Path.GetFullPath(args[4])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-pilots",
                $"p1-{seed}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var databaseDirectory = Path.Combine(outputDirectory, "database");

        try
        {
            var random = new Random(seed);
            using var database = ChronicleDatabase.Open(databaseDirectory);

            // Build a large historical base first. The branch is created at this exact
            // parent boundary, so subsequent parent churn cannot change its inherited view.
            for (var keyId = 0; keyId < baseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(valueBytes, random, salt: keyId));
            }

            var branch = database.CreateBranch("p1-old-thin-branch");
            var branchId = branch.BranchId;
            var branchHistoryId = branch.HistoryId;
            branch.Put(Key(baseKeyCount + 1), Payload(privateBytes, random, salt: 0x51));

            // Parent churn replaces every base value. After exact GC, the old values
            // should remain reconstructable only because the fixed branch base needs them.
            for (var keyId = 0; keyId < baseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(valueBytes, random, salt: keyId + 10_000));
            }

            var gcBeforeDrop = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
            var topology = database.GetHistoryTopologyDiagnostics();
            var branchRoot = topology.RetentionRoots.Single(root =>
                root.Kind.Equals("BranchBase", StringComparison.Ordinal)
                && root.OwnerHistoryId == branchHistoryId);

            var retentionSnapshot = database.CaptureResearchRetentionSnapshot();
            var inspector = new RetentionInspector(retentionSnapshot);
            var explanation = inspector.ExplainRetention(branchRoot.RootId);
            var marginal = explanation.CounterfactualDrop;
            var branchHistory = retentionSnapshot.GetHistory(branchHistoryId);
            var privateLogicalBytes = branchHistory.Versions.Sum(version => version.LogicalPayloadBytes);
            var hra = privateLogicalBytes > 0
                ? (double)marginal.MarginalPayloadBytes / privateLogicalBytes
                : (double?)null;

            branch.Dispose();
            var diagnosticsBefore = database.GetDiagnostics();
            var physical = PhysicalStorageProbe.Measure(
                databaseDirectory,
                () =>
                {
                    database.DeleteBranch(branchId);
                    _ = database.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
                    _ = database.RunCompaction();
                },
                TimeSpan.FromMilliseconds(10));
            var diagnosticsAfter = database.GetDiagnostics();
            var bytesRewritten = Math.Max(
                0,
                diagnosticsAfter.CompactionBytesRewritten - diagnosticsBefore.CompactionBytesRewritten);

            var result = new RetentionPilotResult(
                Pilot: "P1",
                Seed: seed,
                BaseKeyCount: baseKeyCount,
                ValueBytes: valueBytes,
                PrivateRequestedBytes: privateBytes,
                PrivateLogicalBytes: privateLogicalBytes,
                BranchBaseRootId: branchRoot.RootId,
                BranchBaseBoundary: branchRoot.Boundary,
                MarginalProtectedVersionCount: marginal.ProtectedVersionCount - marginal.ProtectedVersionCountAfterDrop,
                MarginalPayloadBytes: marginal.MarginalPayloadBytes,
                MarginalSerializedBytes: marginal.MarginalSerializedBytes,
                CurrentLivePayloadBytes: marginal.CurrentLivePayloadBytes,
                UniqueRequiredVersionCount: marginal.UniqueRequiredVersionCount,
                SharedRequiredVersionCount: marginal.SharedRequiredVersionCount,
                UniqueProtectedPayloadBytes: marginal.UniqueProtectedPayloadBytes,
                SharedProtectedPayloadBytes: marginal.SharedProtectedPayloadBytes,
                HiddenRetentionAmplification: hra,
                GcReclaimedVersionsBeforeDrop: gcBeforeDrop.VersionsReclaimed,
                LogicalFileLengthReduction: physical.LogicalFileLengthReduction,
                AllocatedFilesystemBytesReduction: physical.AllocatedFilesystemBytesReduction,
                AllocationMeasurementExact: physical.Before.AllocationIsExact && physical.After.AllocationIsExact,
                WalBytesReduction: physical.WalBytesReduction,
                CheckpointBytesReduction: physical.CheckpointBytesReduction,
                BytesRewritten: bytesRewritten,
                PeakTemporaryBytes: physical.PeakTemporaryBytes,
                TimeToPhysicalReclaimMilliseconds: physical.Elapsed.TotalMilliseconds,
                ReclamationEfficiency: physical.ReclamationEfficiency(bytesRewritten));

            var resultPath = Path.Combine(outputDirectory, "p1-result.json");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions));
            Console.WriteLine(
                $"P1 PASS marginal={result.MarginalPayloadBytes} private={result.PrivateLogicalBytes} " +
                $"hra={(result.HiddenRetentionAmplification?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")} " +
                $"allocated-released={result.AllocatedFilesystemBytesReduction} rewritten={result.BytesRewritten} " +
                $"output={outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1 FAIL: {exception}");
            return 1;
        }
    }

    private static Guid DeterministicGuid(int seed, int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..4], seed);
        BitConverter.TryWriteBytes(bytes.Slice(4, 4), ordinal);
        BitConverter.TryWriteBytes(bytes.Slice(8, 8), ((long)seed << 32) ^ (uint)ordinal);
        return new Guid(bytes);
    }

    private static byte[] Key(int keyId)
        => BitConverter.GetBytes(keyId);

    private static byte[] Payload(int size, Random random, int salt)
    {
        var value = new byte[size];
        random.NextBytes(value);
        if (value.Length >= sizeof(int))
        {
            BitConverter.GetBytes(salt).CopyTo(value, 0);
        }

        return value;
    }

    private static double MeasureSingleRead(ChronicleBranch branch, byte[] key, bool expectedFound)
    {
        var started = Stopwatch.GetTimestamp();
        var found = branch.TryGet(key, out _);
        var elapsed = Stopwatch.GetTimestamp() - started;
        if (found != expectedFound)
        {
            throw new InvalidOperationException("P3 read probe observed an unexpected visibility result.");
        }

        return elapsed * 1_000_000_000d / Stopwatch.Frequency;
    }

    private static LatencySummary MeasureReads(
        ChronicleBranch branch,
        byte[] key,
        int count,
        bool expectedFound)
    {
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            samples[index] = MeasureSingleRead(branch, key, expectedFound);
        }

        Array.Sort(samples);
        return new LatencySummary(
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples.Average());
    }

    private static double Ratio(double numerator, double denominator)
        => denominator <= 0d ? 0d : numerator / denominator;

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static int UnknownPilot(string value)
    {
        Console.Error.WriteLine($"Unknown pilot '{value}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Research pilots:");
        Console.Error.WriteLine("  pilot P1 <seed> <base-key-count> <value-bytes> <private-bytes> [output-directory]");
        Console.Error.WriteLine("  pilot P1C <seed> <base-key-count> <value-bytes> [output-directory]");
        Console.Error.WriteLine("  pilot P2 <history-count:2..4> [output-directory]");
        Console.Error.WriteLine("  pilot P2R <history-count:2..3> [siblings|chain] [output-directory]");
        Console.Error.WriteLine("  pilot P3 <seed> <reads-per-depth>=10+ [output-directory]");
        Console.Error.WriteLine("  pilot P3T <seed> <reads-per-depth>=100+ [output-directory]");
        Console.Error.WriteLine("  pilot P4 <seed> <history-count:4..64> <requested-index:1..count-1> [output-directory]");
        Console.Error.WriteLine("  pilot P5 <max-depth:1..12> [output-directory]");
        Console.Error.WriteLine("  pilot P6 <seed> [output-directory]");
    }

    private sealed record AncestryTimingDepthPilotResult(
        int Depth,
        double InheritedP50Nanoseconds,
        double InheritedP95Nanoseconds,
        double InheritedP99Nanoseconds,
        double InheritedMeanNanoseconds,
        double LocalP99Nanoseconds,
        double MissingP99Nanoseconds);

    private sealed record AncestryTimingControlPilotResult(
        string Pilot,
        int Seed,
        int ReadsPerDepth,
        IReadOnlyList<AncestryTimingDepthPilotResult> Depths,
        double InheritedP50Amplification,
        double InheritedP95Amplification,
        double InheritedP99Amplification,
        double MissingP99Amplification,
        bool TelemetryDisabled);

    private sealed record RetentionControlPilotResult(
        string Pilot,
        int Seed,
        int BaseKeyCount,
        int ValueBytes,
        long ExpectedBasePayloadBytes,
        long NoChurnDropASingleMarginalBytes,
        long NoChurnDropBothMarginalBytes,
        long ChurnDropASingleMarginalBytes,
        long ChurnDropBSingleMarginalBytes,
        long ChurnDropBothMarginalBytes,
        bool NonAdditiveOverlapObserved,
        bool NoChurnNullControlPassed);

    private sealed record RetentionPilotResult(
        string Pilot,
        int Seed,
        int BaseKeyCount,
        int ValueBytes,
        int PrivateRequestedBytes,
        long PrivateLogicalBytes,
        Guid BranchBaseRootId,
        ulong BranchBaseBoundary,
        int MarginalProtectedVersionCount,
        long MarginalPayloadBytes,
        long MarginalSerializedBytes,
        long CurrentLivePayloadBytes,
        int UniqueRequiredVersionCount,
        int SharedRequiredVersionCount,
        long UniqueProtectedPayloadBytes,
        long SharedProtectedPayloadBytes,
        double? HiddenRetentionAmplification,
        int GcReclaimedVersionsBeforeDrop,
        long LogicalFileLengthReduction,
        long AllocatedFilesystemBytesReduction,
        bool AllocationMeasurementExact,
        long WalBytesReduction,
        long CheckpointBytesReduction,
        long BytesRewritten,
        long PeakTemporaryBytes,
        double TimeToPhysicalReclaimMilliseconds,
        double ReclamationEfficiency);

    private sealed record RealTraceCrashPorPilotResult(
        string Pilot,
        string Topology,
        int HistoryCount,
        int ActionCount,
        int ProductionTraceEventCount,
        IReadOnlyList<string> SharedResources,
        int SharedCatalogActionCount,
        int ExhaustiveOrderCount,
        int ReducedOrderCount,
        int ExhaustiveCrashPlanCount,
        int ReducedCrashPlanCount,
        int ExhaustiveObservationTraceCount,
        int ReducedObservationTraceCount,
        bool ObservationSetsEquivalent,
        double OrderReductionFactor,
        double CrashPlanReductionFactor,
        bool ResourceBaselineUsesSameIndependenceRelation,
        bool ResourceBaselineObservationSetsEquivalent,
        int ResourceBaselineReducedCrashPlanCount,
        double ResourceBaselineCrashPlanReductionFactor,
        int RandomBaselineSampleBudget,
        int RandomBaselineUniqueCrashPlans,
        int RandomBaselineObservationTraceCount,
        double RandomBaselineObservationCoverage,
        bool CatalogBlindObservationSetsEquivalent,
        int CatalogBlindReducedCrashPlanCount,
        double CatalogBlindCrashPlanReductionFactor,
        double ElapsedMilliseconds);

    private sealed record CrashPorPilotResult(
        string Pilot,
        int HistoryCount,
        int ActionCount,
        int ExhaustiveOrderCount,
        int ReducedOrderCount,
        int ExhaustiveCrashPlanCount,
        int ReducedCrashPlanCount,
        int ExhaustiveObservationTraceCount,
        int ReducedObservationTraceCount,
        bool ObservationSetsEquivalent,
        double OrderReductionFactor,
        double CrashPlanReductionFactor,
        int MutantsKilled,
        int MutantCount,
        IReadOnlyList<PersistenceMutantResult> Mutants,
        double ElapsedMilliseconds);




    private sealed record ErasurePilotResult(
        string Pilot,
        int Seed,
        int ObserverHistoryCount,
        int ReachableValueRepresentationCount,
        int BlockingObserverContractCount,
        int MvccVersionOccurrences,
        int SnapshotRootOccurrences,
        int BranchBaseOccurrences,
        int WalOccurrences,
        int CheckpointOccurrences,
        int DerivedStateOccurrences,
        bool MainCurrentStateIsTombstoned,
        string RequestOutcome,
        string AuthorizedForcePhysicalOutcome,
        string AuthorizedForceLogicalOnlyOutcome,
        int RequiredLogicalRevocations,
        int ProposedLogicalRewriteCount,
        bool PhysicalClosureComplete,
        IReadOnlyList<string> UnscannedPhysicalRepresentations);

    private sealed record RecoveryCompositionPilotResult(
        string Pilot,
        string Model,
        int MaxDepth,
        int UniqueStateCount,
        int TransitionCount,
        int SafeModelViolationCount,
        int MutantCount,
        int MutantsRejected,
        IReadOnlyList<RecoveryCompositionMutantSummary> Mutants,
        double ElapsedMilliseconds);

    private sealed record RecoveryCompositionMutantSummary(
        string Name,
        bool IsSafe,
        IReadOnlyList<string> ViolatedInvariants);

    private sealed record RecoveryCompositionMutantPilotResult(
        string Name,
        RecoveryCompositionExplorationResult Result);

    private sealed record RecoverySchedulingPilotResult(
        string Pilot,
        int Seed,
        int HistoryCount,
        int RequestedIndex,
        Guid RequestedHistoryId,
        int DependencyClosureCount,
        long BaselineWorkToRequestedReady,
        long SafeRequestedFirstWorkToRequestedReady,
        long TotalWork,
        double SafeRequestedReadinessSpeedup,
        bool SafePlanPreservesGlobalFailClosedSemantics,
        long AggressiveWorkToRequestedReady,
        bool AggressivePlanPreservesGlobalFailClosedSemantics);

    private sealed record AncestryPilotResult(
        string Pilot,
        int Seed,
        int ReadsPerDepth,
        IReadOnlyList<AncestryDepthPilotResult> Depths,
        double AncestryP99Amplification,
        long ReadCount,
        long LocalReadCount,
        long InheritedReadCount,
        long MissingReadCount,
        long LocalMissCount,
        long TombstoneShadowCount,
        long AncestorProbeCount,
        int MaximumResolvedAncestorDepth,
        int P99ResolvedAncestorDepth,
        double RepeatedLookupOpportunity,
        bool TelemetryComplete);

    private sealed record AncestryDepthPilotResult(
        int Depth,
        double ColdInheritedNanoseconds,
        double InheritedP50Nanoseconds,
        double InheritedP95Nanoseconds,
        double InheritedP99Nanoseconds,
        double InheritedMeanNanoseconds,
        double LocalP99Nanoseconds,
        double MissingP99Nanoseconds,
        int ExpectedAncestorProbes);

    private readonly record struct LatencySummary(
        double P50Nanoseconds,
        double P95Nanoseconds,
        double P99Nanoseconds,
        double MeanNanoseconds);
}
