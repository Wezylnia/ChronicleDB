using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Maintenance;
using ChronicleDB.Storage.History;

if (args.Length > 0 && args[0].Equals("--projection-scale", StringComparison.OrdinalIgnoreCase))
{
    return RunProjectionScale(args[1..]);
}

var options = Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

var fractions = new[] { 0, 25, 50, 75, 100 };
var modes = new[] { ShadowMode.Overwrite, ShadowMode.Tombstone };
var snapshots = new[]
{
    SnapshotMode.None,
    SnapshotMode.PreShadow,
    SnapshotMode.PostShadow,
    SnapshotMode.ActivePreShadow,
    SnapshotMode.ActivePostShadow,
};
var cases = new List<CaseResult>();
var fanoutCases = new List<FanoutCaseResult>();
var nestedCases = new List<NestedCaseResult>();
var physicalCases = new List<PhysicalCaseResult>();
var mixedCases = new List<MixedCaseResult>();
var ordinal = 0;

if (!options.PhysicalOnly)
{
    foreach (var fraction in fractions)
    {
        foreach (var mode in modes)
        {
            foreach (var snapshotMode in snapshots)
            {
                ordinal++;
                var caseDirectory = Path.Combine(options.OutputDirectory, $"case-{ordinal:D2}");
                cases.Add(RunCase(options, fraction, mode, snapshotMode, caseDirectory));
            }
        }
    }

    var fanoutOrdinal = 0;
    foreach (var branchCount in new[] { 1, 2, 4, 8 })
    {
        foreach (var fraction in new[] { 25, 50, 75, 100 })
        {
            foreach (var mode in modes)
            {
                fanoutOrdinal++;
                var caseDirectory = Path.Combine(options.OutputDirectory, $"fanout-{fanoutOrdinal:D2}");
                fanoutCases.Add(RunStaggeredFanoutCase(options, branchCount, fraction, mode, caseDirectory));
            }
        }
    }

    foreach (var depth in new[] { 1, 2, 4, 8, 16 })
    {
        foreach (var fraction in new[] { 50, 100 })
        {
            var caseDirectory = Path.Combine(options.OutputDirectory, $"nested-d{depth:D2}-s{fraction:D3}");
            nestedCases.Add(RunNestedCase(options, depth, fraction, caseDirectory));
        }
    }

    foreach (var seed in new[] { 17, 53 })
    {
        foreach (var branchCount in new[] { 4, 8 })
        {
            foreach (var fraction in new[] { 25, 50, 75 })
            {
                var caseDirectory = Path.Combine(
                    options.OutputDirectory,
                    $"mixed-seed{seed}-b{branchCount:D2}-s{fraction:D3}");
                mixedCases.Add(RunMixedFanoutCase(
                    options,
                    seed,
                    branchCount,
                    fraction,
                    caseDirectory));
            }
        }
    }

}

if (options.RunPhysical)
{
    var physicalPlan = new List<PhysicalTarget>(8);
    if (options.PhysicalTarget is { } target)
    {
        physicalPlan.Add(target);
    }
    else
    {
        foreach (var branchCount in new[] { 4, 8 })
        {
            foreach (var fraction in new[] { 50, 100 })
            {
                foreach (var mode in modes)
                {
                    physicalPlan.Add(new PhysicalTarget(branchCount, fraction, mode));
                }
            }
        }
    }

    foreach (var physicalCase in physicalPlan)
    {
        var caseDirectory = Path.Combine(
            options.OutputDirectory,
            $"physical-b{physicalCase.BranchCount:D2}-s{physicalCase.ShadowPercent:D3}-{physicalCase.Mode.ToString().ToLowerInvariant()}");
        physicalCases.Add(RunPhysicalFanoutCase(
            options,
            physicalCase.BranchCount,
            physicalCase.ShadowPercent,
            physicalCase.Mode,
            caseDirectory));
    }
}

var eligible = cases.Where(item =>
        item.SnapshotMode != SnapshotMode.PreShadow.ToString()
        && item.SnapshotMode != SnapshotMode.ActivePreShadow.ToString()
        && item.ShadowPercent > 0)
    .ToArray();
var allRatios = eligible.Select(item => item.ShadowAwareReclamationRatio)
    .Concat(fanoutCases.Select(item => item.ShadowAwareReclamationRatio))
    .Concat(nestedCases.Select(item => item.ShadowAwareReclamationRatio))
    .Concat(mixedCases.Select(item => item.ShadowAwareReclamationRatio))
    .Concat(physicalCases.Select(item => item.CandidateLogicalReclamationRatio))
    .ToArray();
var maximumReleasedPayloadBytes = new long[]
{
    cases.Count == 0 ? 0 : cases.Max(item => item.ShadowReleasedPayloadBytes),
    fanoutCases.Count == 0 ? 0 : fanoutCases.Max(item => item.ShadowReleasedPayloadBytes),
    nestedCases.Count == 0 ? 0 : nestedCases.Max(item => item.ShadowReleasedPayloadBytes),
    mixedCases.Count == 0 ? 0 : mixedCases.Max(item => item.ShadowReleasedPayloadBytes),
    physicalCases.Count == 0 ? 0 : physicalCases.Max(item => item.CandidateShadowReleasedPayloadBytes),
}.Max();

var result = new PilotResult(
    Pilot: "A1-SHADOW",
    MainCommitUnderTest: options.MainCommit,
    BaseKeyCount: options.BaseKeyCount,
    ValueBytes: options.ValueBytes,
    CaseCount: cases.Count,
    FanoutCaseCount: fanoutCases.Count,
    NestedCaseCount: nestedCases.Count,
    PhysicalCaseCount: physicalCases.Count,
    PhysicalObserverMismatchCount: physicalCases.Count(item => !item.ObserverStateEqualAfterRestart),
    PhysicalAllocationIncompleteCount: physicalCases.Count(item => !item.AllocationMeasurementExact),
    MixedCaseCount: mixedCases.Count,
    CandidateSubsetFailures: cases.Count(item => !item.CandidateIsSubsetOfBaseline)
        + fanoutCases.Count(item => !item.CandidateIsSubsetOfBaseline)
        + nestedCases.Count(item => !item.CandidateIsSubsetOfBaseline)
        + mixedCases.Count(item => !item.CandidateIsSubsetOfBaseline),
    ExpectedReleaseMismatches: cases.Count(item => item.ShadowReleasedPayloadBytes != item.ExpectedReleasedPayloadBytes)
        + fanoutCases.Count(item => item.ShadowReleasedPayloadBytes != item.ExpectedReleasedPayloadBytes)
        + nestedCases.Count(item => item.ShadowReleasedPayloadBytes != item.ExpectedReleasedPayloadBytes)
        + mixedCases.Count(item => item.ShadowReleasedPayloadBytes != item.ExpectedReleasedPayloadBytes),
    PreShadowSafetyFailures: cases.Count(item =>
        (item.SnapshotMode == SnapshotMode.PreShadow.ToString()
            || item.SnapshotMode == SnapshotMode.ActivePreShadow.ToString())
        && item.ShadowReleasedPayloadBytes != 0),
    ObserverEquivalenceFailures: cases.Count(item => !item.ObserverEquivalenceVerified)
        + fanoutCases.Count(item => !item.ObserverEquivalenceVerified)
        + nestedCases.Count(item => !item.ObserverEquivalenceVerified)
        + mixedCases.Count(item => !item.ObserverEquivalenceVerified),
    ObserverMinimalityFailures: cases.Count(item => !item.ObserverMinimalityVerified)
        + fanoutCases.Count(item => !item.ObserverMinimalityVerified)
        + nestedCases.Count(item => !item.ObserverMinimalityVerified)
        + mixedCases.Count(item => !item.ObserverMinimalityVerified),
    MaximumReclamationRatio: allRatios.Length == 0 ? 1d : allRatios.Max(),
    MedianReclamationRatio: Median(allRatios),
    MaximumReleasedPayloadBytes: maximumReleasedPayloadBytes,
    Cases: cases,
    FanoutCases: fanoutCases,
    NestedCases: nestedCases,
    PhysicalCases: physicalCases,
    MixedCases: mixedCases);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
File.WriteAllText(Path.Combine(options.OutputDirectory, "a1-shadow-result.json"), JsonSerializer.Serialize(result, jsonOptions));

var pass = result.CandidateSubsetFailures == 0
    && result.ExpectedReleaseMismatches == 0
    && result.PreShadowSafetyFailures == 0
    && result.ObserverEquivalenceFailures == 0
    && result.ObserverMinimalityFailures == 0
    && result.PhysicalObserverMismatchCount == 0
    && result.PhysicalAllocationIncompleteCount == 0;
Console.WriteLine(
    $"A1-SHADOW {(pass ? "PASS" : "FAIL")} cases={result.CaseCount} " +
    $"median-SAR={result.MedianReclamationRatio:F3}x max-SAR={result.MaximumReclamationRatio:F3}x " +
    $"max-release={result.MaximumReleasedPayloadBytes}B output={options.OutputDirectory}");
return pass ? 0 : 1;


static int RunProjectionScale(string[] args)
{
    if (args.Length < 5
        || !int.TryParse(args[0], out var keyCount)
        || !int.TryParse(args[1], out var branchCount)
        || !int.TryParse(args[2], out var shadowPercent)
        || !Enum.TryParse<ShadowMode>(args[3], ignoreCase: true, out var mode)
        || !int.TryParse(args[4], out var repetitions)
        || keyCount is < 8 or > 65536
        || branchCount is < 1 or > 64
        || shadowPercent is < 1 or > 100
        || repetitions is < 1 or > 20)
    {
        Console.Error.WriteLine(
            "Usage: --projection-scale <keys:8..65536> <branches:1..64> <shadow-percent:1..100> " +
            "<overwrite|tombstone> <repetitions:1..20> [output-directory]");
        return 2;
    }

    var outputDirectory = args.Length >= 6
        ? Path.GetFullPath(args[5])
        : Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "a1-shadow-scale",
            $"k{keyCount}-b{branchCount}-s{shadowPercent}-{mode.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outputDirectory);

    try
    {
        var snapshot = BuildProjectionScaleSnapshot(keyCount, branchCount, shadowPercent, mode);
        // One untimed warmup ensures JIT and first-use static initialization do not
        // define the measured projection curve.
        var warmup = new ShadowAwareRetentionProjection(snapshot).Analyze();
        ValidateProjectionScaleResult(warmup, keyCount, branchCount, shadowPercent, mode);

        var runs = new List<ProjectionScaleRun>(repetitions);
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var result = new ShadowAwareRetentionProjection(snapshot).Analyze();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            ValidateProjectionScaleResult(result, keyCount, branchCount, shadowPercent, mode);
            runs.Add(new ProjectionScaleRun(
                repetition,
                elapsed,
                result.ConstructionMilliseconds,
                result.CoreProjectionMilliseconds,
                result.ObserverVerificationMilliseconds,
                allocated,
                result.BaselineVersionCount,
                result.ShadowAwareVersionCount,
                result.ShadowReleasedPayloadBytes,
                result.ShadowAwareReclamationRatio,
                result.ObserverEquivalenceCheckCount,
                result.ObserverKeyResolutionCount,
                result.ParentFallbackHops));
        }

        var orderedMs = runs.Select(run => run.VerifiedProjectionMilliseconds).Order().ToArray();
        var orderedAllocated = runs.Select(run => (double)run.ThreadAllocatedBytes).Order().ToArray();
        var first = runs[0];
        var summary = new ProjectionScaleResult(
            Pilot: "A1-SHADOW-PROJECTION-SCALE",
            KeyCount: keyCount,
            BranchCount: branchCount,
            ShadowPercent: shadowPercent,
            Mode: mode.ToString(),
            Repetitions: repetitions,
            VersionCount: snapshot.Histories.Sum(history => history.Versions.Count),
            BaselineVersionCount: first.BaselineVersionCount,
            ShadowAwareVersionCount: first.ShadowAwareVersionCount,
            ShadowReleasedPayloadBytes: first.ShadowReleasedPayloadBytes,
            ShadowAwareReclamationRatio: first.ShadowAwareReclamationRatio,
            ObserverEquivalenceCheckCount: first.ObserverEquivalenceCheckCount,
            ObserverKeyResolutionCount: first.ObserverKeyResolutionCount,
            ParentFallbackHops: first.ParentFallbackHops,
            MedianVerifiedProjectionMilliseconds: Percentile(orderedMs, 0.50),
            P95VerifiedProjectionMilliseconds: Percentile(orderedMs, 0.95),
            MedianConstructionMilliseconds: Median(runs.Select(run => run.ConstructionMilliseconds)),
            MedianCoreProjectionMilliseconds: Median(runs.Select(run => run.CoreProjectionMilliseconds)),
            MedianObserverVerificationMilliseconds: Median(runs.Select(run => run.ObserverVerificationMilliseconds)),
            MedianThreadAllocatedBytes: (long)Percentile(orderedAllocated, 0.50),
            P95ThreadAllocatedBytes: (long)Percentile(orderedAllocated, 0.95),
            Runs: runs);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "projection-scale-result.json"),
            JsonSerializer.Serialize(summary, jsonOptions));
        Console.WriteLine(
            $"A1-SHADOW-PROJECTION-SCALE PASS keys={keyCount} branches={branchCount} " +
            $"shadow={shadowPercent}% mode={mode} versions={summary.VersionCount} " +
            $"median={summary.MedianVerifiedProjectionMilliseconds:F2}ms " +
            $"core={summary.MedianCoreProjectionMilliseconds:F2}ms verify={summary.MedianObserverVerificationMilliseconds:F2}ms " +
            $"alloc={summary.MedianThreadAllocatedBytes}B SAR={summary.ShadowAwareReclamationRatio:F3}x " +
            $"output={outputDirectory}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"A1-SHADOW-PROJECTION-SCALE FAIL: {exception}");
        return 1;
    }
}

static ResearchRetentionSnapshot BuildProjectionScaleSnapshot(
    int keyCount,
    int branchCount,
    int shadowPercent,
    ShadowMode mode)
{
    const int valueBytes = 4096;
    var mainHistoryId = DeterministicGuid(1);
    var histories = new List<ResearchHistoryRetentionSnapshot>(branchCount + 1);
    var roots = new List<ResearchPersistentRetentionRootSnapshot>(branchCount);
    var mainVersions = new List<ResearchCommittedVersionSnapshot>((branchCount + 1) * keyCount);

    for (var generation = 1; generation <= branchCount + 1; generation++)
    {
        for (var keyId = 0; keyId < keyCount; keyId++)
        {
            mainVersions.Add(new ResearchCommittedVersionSnapshot(
                VersionId: $"main:g{generation}:k{keyId}",
                TransactionId: DeterministicGuid(checked(10_000 + generation)),
                CommitSequence: (ulong)generation,
                KeyId: $"k{keyId:D8}",
                KeyBytes: 8,
                ValueBytes: valueBytes,
                IsTombstone: false));
        }
    }

    histories.Add(new ResearchHistoryRetentionSnapshot(
        mainHistoryId,
        RetentionFloor: (ulong)(branchCount + 1),
        CurrentSequence: (ulong)(branchCount + 1),
        Versions: Array.AsReadOnly(mainVersions.ToArray())));

    var shadowKeyCount = keyCount * shadowPercent / 100;
    for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
    {
        var historyId = DeterministicGuid(checked(100 + branchIndex));
        var branchVersions = new List<ResearchCommittedVersionSnapshot>(shadowKeyCount);
        for (var keyId = 0; keyId < shadowKeyCount; keyId++)
        {
            branchVersions.Add(new ResearchCommittedVersionSnapshot(
                VersionId: $"branch{branchIndex}:k{keyId}",
                TransactionId: DeterministicGuid(checked(20_000 + branchIndex)),
                CommitSequence: 1,
                KeyId: $"k{keyId:D8}",
                KeyBytes: 8,
                ValueBytes: mode == ShadowMode.Tombstone ? 0 : valueBytes,
                IsTombstone: mode == ShadowMode.Tombstone));
        }

        histories.Add(new ResearchHistoryRetentionSnapshot(
            historyId,
            RetentionFloor: 1,
            CurrentSequence: 1,
            Versions: Array.AsReadOnly(branchVersions.ToArray())));
        roots.Add(new ResearchPersistentRetentionRootSnapshot(
            RootId: DeterministicGuid(checked(1_000 + branchIndex)),
            Kind: "BranchBase",
            OwnerHistoryId: historyId,
            ProtectedHistoryId: mainHistoryId,
            Boundary: (ulong)(branchIndex + 1)));
    }

    return new ResearchRetentionSnapshot(
        Array.AsReadOnly(histories.ToArray()),
        Array.AsReadOnly(roots.ToArray()),
        Array.Empty<ResearchActiveRetentionBoundarySnapshot>());
}

static void ValidateProjectionScaleResult(
    ShadowAwareRetentionProjectionResult result,
    int keyCount,
    int branchCount,
    int shadowPercent,
    ShadowMode mode)
{
    var shadowKeyCount = keyCount * shadowPercent / 100;
    var expectedRelease = checked((long)branchCount * shadowKeyCount * 4096L);
    var effect = ShadowRetentionEffectModel.Predict(
        keyCount,
        branchCount,
        (double)shadowKeyCount / keyCount,
        tombstoneFraction: mode == ShadowMode.Tombstone ? 1d : 0d,
        valueBytes: 4096);
    var expectedRatio = effect.ShadowAwareReclamationRatio;
    if (!result.CandidateIsSubsetOfBaseline
        || !result.FlatExactBaselineVerified
        || !result.ObserverEquivalenceVerified
        || !result.ObserverMinimalityVerified
        || result.ShadowReleasedPayloadBytes != expectedRelease
        || Math.Abs(result.ShadowAwareReclamationRatio - expectedRatio) > 1e-12)
    {
        throw new InvalidOperationException(
            $"Projection scale invariant failed: release={result.ShadowReleasedPayloadBytes}, expected={expectedRelease}, " +
            $"subset={result.CandidateIsSubsetOfBaseline}, flatExact={result.FlatExactBaselineVerified}, equivalence={result.ObserverEquivalenceVerified}, " +
            $"minimal={result.ObserverMinimalityVerified}, ratio={result.ShadowAwareReclamationRatio:F6}, " +
            $"expectedRatio={expectedRatio:F6}.");
    }
}

static Guid DeterministicGuid(int value)
{
    Span<byte> bytes = stackalloc byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], unchecked(value * 397));
    BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], unchecked(value * 7919));
    BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], unchecked(value * 104729));
    return new Guid(bytes);
}

static double Percentile(double[] ordered, double percentile)
{
    if (ordered.Length == 0)
    {
        return 0d;
    }

    if (ordered.Length == 1)
    {
        return ordered[0];
    }

    var position = percentile * (ordered.Length - 1);
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    if (lower == upper)
    {
        return ordered[lower];
    }

    var weight = position - lower;
    return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight);
}

static CaseResult RunCase(
    Options options,
    int shadowPercent,
    ShadowMode mode,
    SnapshotMode snapshotMode,
    string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }

    Directory.CreateDirectory(directory);
    var databaseDirectory = Path.Combine(directory, "db");
    var shadowCount = options.BaseKeyCount * shadowPercent / 100;
    Guid branchHistoryId;
    ulong branchBaseBoundary;

    using (var database = ChronicleDatabase.Open(databaseDirectory))
    {
        for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
        {
            database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 1));
        }

        branchBaseBoundary = database.CurrentCommitSequence.Value;
        using var branch = database.CreateBranch($"shadow-{shadowPercent}-{mode}-{snapshotMode}");
        branchHistoryId = branch.HistoryId;

        ChronicleBranchSnapshot? retainedSnapshot = null;
        ChronicleBranchHistoricalView? retainedHistoricalView = null;
        try
        {
            if (snapshotMode == SnapshotMode.PreShadow)
            {
                retainedSnapshot = branch.CreateSnapshot("pre-shadow");
            }
            else if (snapshotMode == SnapshotMode.ActivePreShadow)
            {
                retainedHistoricalView = branch.OpenHistoricalView(branch.CurrentSequence);
            }

            for (var keyId = 0; keyId < shadowCount; keyId++)
            {
                if (mode == ShadowMode.Overwrite)
                {
                    branch.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 7));
                }
                else if (!branch.Delete(Key(keyId)))
                {
                    throw new InvalidOperationException($"Inherited key {keyId} was unexpectedly absent before tombstoning.");
                }
            }

            if (snapshotMode == SnapshotMode.PostShadow)
            {
                retainedSnapshot = branch.CreateSnapshot("post-shadow");
            }
            else if (snapshotMode == SnapshotMode.ActivePostShadow)
            {
                retainedHistoricalView = branch.OpenHistoricalView(branch.CurrentSequence);
            }

            // Advance Main for every key so its branch-base predecessor is not also
            // required as Main's current/floor-visible version in the evaluation.
            for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 2));
            }

            var raw = database.CaptureResearchRetentionSnapshot();
            var evaluation = raw with
            {
                Histories = raw.Histories
                    .Select(history => history with { RetentionFloor = history.CurrentSequence })
                    .ToArray(),
            };

            var branchRoot = evaluation.PersistentRoots.Single(root =>
                root.Kind.Equals("BranchBase", StringComparison.Ordinal)
                && root.OwnerHistoryId == branchHistoryId);
            if (branchRoot.Boundary != branchBaseBoundary)
            {
                throw new InvalidOperationException("Captured BranchBase boundary differs from the creation boundary.");
            }

            var analysis = new ShadowAwareRetentionProjection(evaluation).Analyze();
            var expectedRelease = snapshotMode is SnapshotMode.PreShadow or SnapshotMode.ActivePreShadow
                ? 0L
                : checked((long)shadowCount * options.ValueBytes);

            var caseResult = new CaseResult(
                ShadowPercent: shadowPercent,
                ShadowKeyCount: shadowCount,
                Mode: mode.ToString(),
                SnapshotMode: snapshotMode.ToString(),
                BaselineVersionCount: analysis.BaselineVersionCount,
                ShadowAwareVersionCount: analysis.ShadowAwareVersionCount,
                BaselinePayloadBytes: analysis.BaselinePayloadBytes,
                ShadowAwarePayloadBytes: analysis.ShadowAwarePayloadBytes,
                ShadowReleasedPayloadBytes: analysis.ShadowReleasedPayloadBytes,
                ExpectedReleasedPayloadBytes: expectedRelease,
                ShadowAwareReclamationRatio: analysis.ShadowAwareReclamationRatio,
                CandidateIsSubsetOfBaseline: analysis.CandidateIsSubsetOfBaseline,
                ObserverEquivalenceVerified: analysis.ObserverEquivalenceVerified,
                ObserverEquivalenceCheckCount: analysis.ObserverEquivalenceCheckCount,
                ObserverMinimalityVerified: analysis.ObserverMinimalityVerified,
                UnwitnessedRequiredVersionCount: analysis.UnwitnessedRequiredVersionIds.Count,
                ReleasedVersionCount: analysis.ReleasedVersionIds.Count,
                ParentFallbackHops: analysis.ParentFallbackHops,
                LocalShadowStops: analysis.LocalShadowStops);

            File.WriteAllText(
                Path.Combine(directory, "case-result.json"),
                JsonSerializer.Serialize(caseResult, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }));
            return caseResult;
        }
        finally
        {
            retainedHistoricalView?.Dispose();
            retainedSnapshot?.Dispose();
        }
    }
}


static FanoutCaseResult RunStaggeredFanoutCase(
    Options options,
    int branchCount,
    int shadowPercent,
    ShadowMode mode,
    string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }

    Directory.CreateDirectory(directory);
    var databaseDirectory = Path.Combine(directory, "db");
    var shadowCount = options.BaseKeyCount * shadowPercent / 100;
    var branchHistoryIds = new List<Guid>(branchCount);

    using var database = ChronicleDatabase.Open(databaseDirectory);
    for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
    {
        database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 1));
    }

    var branches = new List<ChronicleBranch>(branchCount);
    try
    {
        for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
        {
            var branch = database.CreateBranch($"fanout-{branchCount}-{shadowPercent}-{mode}-{branchIndex:D2}");
            branches.Add(branch);
            branchHistoryIds.Add(branch.HistoryId);

            for (var keyId = 0; keyId < shadowCount; keyId++)
            {
                if (mode == ShadowMode.Overwrite)
                {
                    branch.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: checked(100 + branchIndex)));
                }
                else if (!branch.Delete(Key(keyId)))
                {
                    throw new InvalidOperationException($"Fanout inherited key {keyId} was unexpectedly absent.");
                }
            }

            // Move Main to a fresh full-key generation before creating the next
            // branch. This makes each BranchBase protect a distinct predecessor.
            for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: checked(2 + branchIndex)));
            }
        }

        var raw = database.CaptureResearchRetentionSnapshot();
        var evaluation = raw with
        {
            Histories = raw.Histories
                .Select(history => history with { RetentionFloor = history.CurrentSequence })
                .ToArray(),
        };

        var capturedBranchRoots = evaluation.PersistentRoots.Count(root =>
            root.Kind.Equals("BranchBase", StringComparison.Ordinal)
            && branchHistoryIds.Contains(root.OwnerHistoryId));
        if (capturedBranchRoots != branchCount)
        {
            throw new InvalidOperationException(
                $"Expected {branchCount} BranchBase roots but captured {capturedBranchRoots}.");
        }

        var analysis = new ShadowAwareRetentionProjection(evaluation).Analyze();
        var expectedRelease = checked((long)branchCount * shadowCount * options.ValueBytes);
        var result = new FanoutCaseResult(
            BranchCount: branchCount,
            ShadowPercent: shadowPercent,
            ShadowKeyCount: shadowCount,
            Mode: mode.ToString(),
            BaselineVersionCount: analysis.BaselineVersionCount,
            ShadowAwareVersionCount: analysis.ShadowAwareVersionCount,
            BaselinePayloadBytes: analysis.BaselinePayloadBytes,
            ShadowAwarePayloadBytes: analysis.ShadowAwarePayloadBytes,
            ShadowReleasedPayloadBytes: analysis.ShadowReleasedPayloadBytes,
            ExpectedReleasedPayloadBytes: expectedRelease,
            ShadowAwareReclamationRatio: analysis.ShadowAwareReclamationRatio,
            CandidateIsSubsetOfBaseline: analysis.CandidateIsSubsetOfBaseline,
            ObserverEquivalenceVerified: analysis.ObserverEquivalenceVerified,
            ObserverEquivalenceCheckCount: analysis.ObserverEquivalenceCheckCount,
            ObserverMinimalityVerified: analysis.ObserverMinimalityVerified,
            UnwitnessedRequiredVersionCount: analysis.UnwitnessedRequiredVersionIds.Count,
            ReleasedVersionCount: analysis.ReleasedVersionIds.Count,
            ParentFallbackHops: analysis.ParentFallbackHops);

        File.WriteAllText(
            Path.Combine(directory, "case-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
        return result;
    }
    finally
    {
        foreach (var branch in branches.AsEnumerable().Reverse())
        {
            branch.Dispose();
        }
    }
}


static NestedCaseResult RunNestedCase(
    Options options,
    int depth,
    int shadowPercent,
    string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }

    Directory.CreateDirectory(directory);
    var databaseDirectory = Path.Combine(directory, "db");
    var shadowCount = options.BaseKeyCount * shadowPercent / 100;

    using var database = ChronicleDatabase.Open(databaseDirectory);
    for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
    {
        database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 1));
    }

    var branches = new List<ChronicleBranch>(depth);
    ChronicleBranch? parent = null;
    try
    {
        for (var level = 1; level <= depth; level++)
        {
            var child = parent is null
                ? database.CreateBranch($"nested-{depth}-{shadowPercent}-{level:D2}")
                : parent.CreateBranch($"nested-{depth}-{shadowPercent}-{level:D2}");
            branches.Add(child);

            for (var keyId = 0; keyId < shadowCount; keyId++)
            {
                child.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: checked(1000 + level)));
            }

            // Make this edge's base predecessor distinct from the parent's current
            // generic requirement, but only for the keys under attack.
            for (var keyId = 0; keyId < shadowCount; keyId++)
            {
                var payload = Payload(options.ValueBytes, keyId, generation: checked(2000 + level));
                if (parent is null)
                {
                    database.Put(Key(keyId), payload);
                }
                else
                {
                    parent.Put(Key(keyId), payload);
                }
            }

            parent = child;
        }

        var raw = database.CaptureResearchRetentionSnapshot();
        var evaluation = raw with
        {
            Histories = raw.Histories
                .Select(history => history with { RetentionFloor = history.CurrentSequence })
                .ToArray(),
        };
        var analysis = new ShadowAwareRetentionProjection(evaluation).Analyze();
        var expectedRelease = checked((long)depth * shadowCount * options.ValueBytes);
        var result = new NestedCaseResult(
            Depth: depth,
            ShadowPercent: shadowPercent,
            ShadowKeyCount: shadowCount,
            BaselineVersionCount: analysis.BaselineVersionCount,
            ShadowAwareVersionCount: analysis.ShadowAwareVersionCount,
            BaselinePayloadBytes: analysis.BaselinePayloadBytes,
            ShadowAwarePayloadBytes: analysis.ShadowAwarePayloadBytes,
            ShadowReleasedPayloadBytes: analysis.ShadowReleasedPayloadBytes,
            ExpectedReleasedPayloadBytes: expectedRelease,
            ShadowAwareReclamationRatio: analysis.ShadowAwareReclamationRatio,
            CandidateIsSubsetOfBaseline: analysis.CandidateIsSubsetOfBaseline,
            ObserverEquivalenceVerified: analysis.ObserverEquivalenceVerified,
            ObserverEquivalenceCheckCount: analysis.ObserverEquivalenceCheckCount,
            ObserverMinimalityVerified: analysis.ObserverMinimalityVerified,
            UnwitnessedRequiredVersionCount: analysis.UnwitnessedRequiredVersionIds.Count,
            ReleasedVersionCount: analysis.ReleasedVersionIds.Count,
            ParentFallbackHops: analysis.ParentFallbackHops);

        File.WriteAllText(
            Path.Combine(directory, "case-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
        return result;
    }
    finally
    {
        foreach (var branch in branches.AsEnumerable().Reverse())
        {
            branch.Dispose();
        }
    }
}

static MixedCaseResult RunMixedFanoutCase(
    Options options,
    int seed,
    int branchCount,
    int shadowPercent,
    string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }

    Directory.CreateDirectory(directory);
    var databaseDirectory = Path.Combine(directory, "db");
    var random = new Random(seed);
    var shadowCount = options.BaseKeyCount * shadowPercent / 100;
    var overwriteCount = 0;
    var tombstoneCount = 0;

    using var database = ChronicleDatabase.Open(databaseDirectory);
    for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
    {
        database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 1));
    }

    var branches = new List<ChronicleBranch>(branchCount);
    try
    {
        for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
        {
            var branch = database.CreateBranch($"mixed-{seed}-{branchCount}-{shadowPercent}-{branchIndex:D2}");
            branches.Add(branch);
            var selectedKeys = Enumerable.Range(0, options.BaseKeyCount)
                .OrderBy(_ => random.Next())
                .Take(shadowCount)
                .ToArray();
            foreach (var keyId in selectedKeys)
            {
                if (random.Next(2) == 0)
                {
                    branch.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: checked(5000 + branchIndex)));
                    overwriteCount++;
                }
                else
                {
                    _ = branch.Delete(Key(keyId));
                    tombstoneCount++;
                }
            }

            for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
            {
                database.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: checked(6000 + branchIndex)));
            }
        }

        var raw = database.CaptureResearchRetentionSnapshot();
        var evaluation = raw with
        {
            Histories = raw.Histories
                .Select(history => history with { RetentionFloor = history.CurrentSequence })
                .ToArray(),
        };
        var analysis = new ShadowAwareRetentionProjection(evaluation).Analyze();
        var expectedRelease = checked((long)branchCount * shadowCount * options.ValueBytes);
        var result = new MixedCaseResult(
            Seed: seed,
            BranchCount: branchCount,
            ShadowPercent: shadowPercent,
            ShadowOperations: branchCount * shadowCount,
            OverwriteCount: overwriteCount,
            TombstoneCount: tombstoneCount,
            BaselinePayloadBytes: analysis.BaselinePayloadBytes,
            ShadowAwarePayloadBytes: analysis.ShadowAwarePayloadBytes,
            ShadowReleasedPayloadBytes: analysis.ShadowReleasedPayloadBytes,
            ExpectedReleasedPayloadBytes: expectedRelease,
            ShadowAwareReclamationRatio: analysis.ShadowAwareReclamationRatio,
            CandidateIsSubsetOfBaseline: analysis.CandidateIsSubsetOfBaseline,
            ObserverEquivalenceVerified: analysis.ObserverEquivalenceVerified,
            ObserverEquivalenceCheckCount: analysis.ObserverEquivalenceCheckCount,
            ObserverMinimalityVerified: analysis.ObserverMinimalityVerified,
            UnwitnessedRequiredVersionCount: analysis.UnwitnessedRequiredVersionIds.Count);

        File.WriteAllText(
            Path.Combine(directory, "case-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
        return result;
    }
    finally
    {
        foreach (var branch in branches.AsEnumerable().Reverse())
        {
            branch.Dispose();
        }
    }
}

static PhysicalCaseResult RunPhysicalFanoutCase(
    Options options,
    int branchCount,
    int shadowPercent,
    ShadowMode mode,
    string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }

    Directory.CreateDirectory(directory);
    var sourceDirectory = Path.Combine(directory, "source");
    var baselineDirectory = Path.Combine(directory, "baseline");
    var candidateDirectory = Path.Combine(directory, "candidate");
    var shadowCount = options.BaseKeyCount * shadowPercent / 100;
    var branchIds = new List<Guid>(branchCount);

    using (var database = ChronicleDatabase.Open(sourceDirectory))
    {
        using (var baseTransaction = database.BeginTransaction())
        {
            for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
            {
                baseTransaction.Put(Key(keyId), Payload(options.ValueBytes, keyId, generation: 1));
            }

            baseTransaction.Commit();
        }

        var branches = new List<ChronicleBranch>(branchCount);
        try
        {
            for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                var branch = database.CreateBranch($"physical-{branchCount}-{shadowPercent}-{mode}-{branchIndex:D2}");
                branches.Add(branch);
                branchIds.Add(branch.BranchId);
                using (var branchTransaction = branch.BeginTransaction())
                {
                    for (var keyId = 0; keyId < shadowCount; keyId++)
                    {
                        if (mode == ShadowMode.Overwrite)
                        {
                            branchTransaction.Put(
                                Key(keyId),
                                Payload(options.ValueBytes, keyId, generation: checked(3000 + branchIndex)));
                        }
                        else
                        {
                            branchTransaction.Delete(Key(keyId));
                        }
                    }

                    branchTransaction.Commit();
                }

                using var mainTransaction = database.BeginTransaction();
                for (var keyId = 0; keyId < options.BaseKeyCount; keyId++)
                {
                    mainTransaction.Put(
                        Key(keyId),
                        Payload(options.ValueBytes, keyId, generation: checked(4000 + branchIndex)));
                }

                mainTransaction.Commit();
            }
        }
        finally
        {
            foreach (var branch in branches.AsEnumerable().Reverse())
            {
                branch.Dispose();
            }
        }
    }

    CopyDirectory(sourceDirectory, baselineDirectory);
    CopyDirectory(sourceDirectory, candidateDirectory);

    GarbageCollectionResult baselineGc;
    CompactionResult baselineCompaction;
    double baselineGcMilliseconds;
    double baselineCompactionMilliseconds;
    using (var baseline = ChronicleDatabase.Open(baselineDirectory))
    {
        var started = Stopwatch.GetTimestamp();
        baselineGc = baseline.RunGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
        baselineGcMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        baselineCompaction = baseline.RunCompaction(new CompactionOptions
        {
            MaxHistoriesPerPass = branchCount + 1,
            MinimumReclaimableBytes = 1,
        });
        baselineCompactionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    ShadowAwareGarbageCollectionResult candidateGc;
    CompactionResult candidateCompaction;
    double candidateGcMilliseconds;
    double candidateCompactionMilliseconds;
    using (var candidate = ChronicleDatabase.Open(candidateDirectory))
    {
        var started = Stopwatch.GetTimestamp();
        candidateGc = candidate.RunShadowAwareGarbageCollection(new GarbageCollectionOptions { RetainRecentCommits = 0 });
        candidateGcMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        candidateCompaction = candidate.RunCompaction(new CompactionOptions
        {
            MaxHistoriesPerPass = branchCount + 1,
            MinimumReclaimableBytes = 1,
        });
        candidateCompactionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    var observerStateEqual = CompareCurrentObserverState(
        baselineDirectory,
        candidateDirectory,
        branchIds,
        options.BaseKeyCount);
    var baselinePhysical = PhysicalStorageProbe.Capture(baselineDirectory);
    var candidatePhysical = PhysicalStorageProbe.Capture(candidateDirectory);
    var baselineCheckpointLogical = HistoryCheckpointBytes(baselinePhysical, allocated: false);
    var candidateCheckpointLogical = HistoryCheckpointBytes(candidatePhysical, allocated: false);
    var baselineCheckpointAllocated = HistoryCheckpointBytes(baselinePhysical, allocated: true);
    var candidateCheckpointAllocated = HistoryCheckpointBytes(candidatePhysical, allocated: true);

    var result = new PhysicalCaseResult(
        BranchCount: branchCount,
        ShadowPercent: shadowPercent,
        ShadowKeyCount: shadowCount,
        Mode: mode.ToString(),
        BaselineGcReclaimedVersions: baselineGc.VersionsReclaimed,
        CandidateGcReclaimedVersions: candidateGc.ReclaimedVersions,
        CandidateShadowReleasedPayloadBytes: candidateGc.ShadowReleasedPayloadBytes,
        CandidateLogicalReclamationRatio: candidateGc.ShadowAwareReclamationRatio,
        CandidateSerializedReclamationRatio: candidateGc.ShadowAwareSerializedReclamationRatio,
        BaselineGcMilliseconds: baselineGcMilliseconds,
        CandidateGcMilliseconds: candidateGcMilliseconds,
        CandidateProjectionAnalysisMilliseconds: candidateGc.ProjectionAnalysisMilliseconds,
        CandidateProjectionConstructionMilliseconds: candidateGc.ProjectionConstructionMilliseconds,
        CandidateCoreProjectionMilliseconds: candidateGc.CoreProjectionMilliseconds,
        CandidateObserverVerificationMilliseconds: candidateGc.ObserverVerificationMilliseconds,
        BaselineCompactionMilliseconds: baselineCompactionMilliseconds,
        CandidateCompactionMilliseconds: candidateCompactionMilliseconds,
        BaselineCheckpointLogicalBytes: baselineCheckpointLogical,
        CandidateCheckpointLogicalBytes: candidateCheckpointLogical,
        CheckpointLogicalReductionBytes: Math.Max(0, baselineCheckpointLogical - candidateCheckpointLogical),
        BaselineCheckpointAllocatedBytes: baselineCheckpointAllocated,
        CandidateCheckpointAllocatedBytes: candidateCheckpointAllocated,
        CheckpointAllocatedReductionBytes: Math.Max(0, baselineCheckpointAllocated - candidateCheckpointAllocated),
        BaselineTotalAllocatedBytes: baselinePhysical.AllocatedBytes,
        CandidateTotalAllocatedBytes: candidatePhysical.AllocatedBytes,
        TotalAllocatedReductionBytes: Math.Max(0, baselinePhysical.AllocatedBytes - candidatePhysical.AllocatedBytes),
        BaselineCompactionBytesReclaimed: baselineCompaction.BytesReclaimed,
        CandidateCompactionBytesReclaimed: candidateCompaction.BytesReclaimed,
        AllocationMeasurementExact: baselinePhysical.AllocationIsExact && candidatePhysical.AllocationIsExact,
        ObserverStateEqualAfterRestart: observerStateEqual);

    File.WriteAllText(
        Path.Combine(directory, "case-result.json"),
        JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
    return result;
}

static bool CompareCurrentObserverState(
    string baselineDirectory,
    string candidateDirectory,
    IReadOnlyList<Guid> branchIds,
    int keyCount)
{
    using var baseline = ChronicleDatabase.Open(baselineDirectory);
    using var candidate = ChronicleDatabase.Open(candidateDirectory);
    for (var keyId = 0; keyId < keyCount; keyId++)
    {
        var key = Key(keyId);
        var baselineFound = baseline.TryGet(key, out var baselineValue);
        var candidateFound = candidate.TryGet(key, out var candidateValue);
        if (baselineFound != candidateFound
            || (baselineFound && !baselineValue.AsSpan().SequenceEqual(candidateValue)))
        {
            return false;
        }
    }

    foreach (var branchId in branchIds)
    {
        using var baselineBranch = baseline.OpenBranch(branchId);
        using var candidateBranch = candidate.OpenBranch(branchId);
        for (var keyId = 0; keyId < keyCount; keyId++)
        {
            var key = Key(keyId);
            var baselineFound = baselineBranch.TryGet(key, out var baselineValue);
            var candidateFound = candidateBranch.TryGet(key, out var candidateValue);
            if (baselineFound != candidateFound
                || (baselineFound && !baselineValue.AsSpan().SequenceEqual(candidateValue)))
            {
                return false;
            }
        }
    }

    return true;
}

static long HistoryCheckpointBytes(ResearchPhysicalStorageSnapshot snapshot, bool allocated)
    => snapshot.Files
        .Where(file => file.RelativePath.EndsWith(PersistentHistoryCheckpoint.FileName, StringComparison.OrdinalIgnoreCase))
        .Sum(file => allocated ? file.AllocatedBytes : file.LogicalLengthBytes);

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static Options Parse(string[] args)
{
    var baseKeyCount = 0;
    var valueBytes = 0;
    if (args.Length < 2
        || !int.TryParse(args[0], out baseKeyCount)
        || !int.TryParse(args[1], out valueBytes)
        || baseKeyCount is < 8 or > 4096
        || valueBytes is < 1 or > 1_048_576)
    {
        Console.Error.WriteLine("Usage: <base-key-count:8..4096> <value-bytes:1..1048576> [output-directory] [main-commit] [--physical|--physical-only|--physical-case <branches:1..64> <shadow-percent:1..100> <overwrite|tombstone>]");
        Environment.Exit(2);
    }

    var output = args.Length >= 3
        ? Path.GetFullPath(args[2])
        : Path.Combine(Environment.CurrentDirectory, "artifacts", "a1-shadow", Guid.NewGuid().ToString("N"));
    var commit = args.Length >= 4 ? args[3] : "5fa3d3835c42e929cef14ab90288e04b9e5c113b";
    var physicalOnly = args.Length >= 5 && args[4].Equals("--physical-only", StringComparison.OrdinalIgnoreCase);
    var runPhysical = physicalOnly
        || (args.Length >= 5 && args[4].Equals("--physical", StringComparison.OrdinalIgnoreCase));
    PhysicalTarget? physicalTarget = null;
    if (args.Length >= 5 && args[4].Equals("--physical-case", StringComparison.OrdinalIgnoreCase))
    {
        var branchCount = 0;
        var shadowPercent = 0;
        var mode = ShadowMode.Overwrite;
        if (args.Length < 8
            || !int.TryParse(args[5], out branchCount)
            || branchCount is < 1 or > 64
            || !int.TryParse(args[6], out shadowPercent)
            || shadowPercent is < 1 or > 100
            || !Enum.TryParse(args[7], ignoreCase: true, out mode))
        {
            Console.Error.WriteLine("Usage: --physical-case <branches:1..64> <shadow-percent:1..100> <overwrite|tombstone>");
            Environment.Exit(2);
        }

        physicalTarget = new PhysicalTarget(branchCount, shadowPercent, mode);
        physicalOnly = true;
        runPhysical = true;
    }

    return new Options(baseKeyCount, valueBytes, output, commit, runPhysical, physicalOnly, physicalTarget);
}

static byte[] Key(int value)
{
    var bytes = new byte[8];
    BinaryPrimitives.WriteInt64BigEndian(bytes, value);
    return bytes;
}

static byte[] Payload(int bytes, int keyId, int generation)
{
    var result = new byte[bytes];
    if (bytes >= 8)
    {
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), keyId);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4, 4), generation);
    }
    else
    {
        for (var index = 0; index < bytes; index++)
        {
            result[index] = (byte)(keyId + generation + index);
        }
    }

    return result;
}

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    if (sorted.Length == 0)
    {
        return 1d;
    }

    var middle = sorted.Length / 2;
    return sorted.Length % 2 == 0
        ? (sorted[middle - 1] + sorted[middle]) / 2d
        : sorted[middle];
}

internal sealed record ProjectionScaleRun(
    int Repetition,
    double VerifiedProjectionMilliseconds,
    double ConstructionMilliseconds,
    double CoreProjectionMilliseconds,
    double ObserverVerificationMilliseconds,
    long ThreadAllocatedBytes,
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long ShadowReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    int ObserverEquivalenceCheckCount,
    int ObserverKeyResolutionCount,
    int ParentFallbackHops);

internal sealed record ProjectionScaleResult(
    string Pilot,
    int KeyCount,
    int BranchCount,
    int ShadowPercent,
    string Mode,
    int Repetitions,
    int VersionCount,
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long ShadowReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    int ObserverEquivalenceCheckCount,
    int ObserverKeyResolutionCount,
    int ParentFallbackHops,
    double MedianVerifiedProjectionMilliseconds,
    double P95VerifiedProjectionMilliseconds,
    double MedianConstructionMilliseconds,
    double MedianCoreProjectionMilliseconds,
    double MedianObserverVerificationMilliseconds,
    long MedianThreadAllocatedBytes,
    long P95ThreadAllocatedBytes,
    IReadOnlyList<ProjectionScaleRun> Runs);

internal sealed record Options(
    int BaseKeyCount,
    int ValueBytes,
    string OutputDirectory,
    string MainCommit,
    bool RunPhysical,
    bool PhysicalOnly,
    PhysicalTarget? PhysicalTarget);

internal sealed record PhysicalTarget(
    int BranchCount,
    int ShadowPercent,
    ShadowMode Mode);

internal enum ShadowMode : byte
{
    Overwrite = 1,
    Tombstone = 2,
}

internal enum SnapshotMode : byte
{
    None = 1,
    PreShadow = 2,
    PostShadow = 3,
    ActivePreShadow = 4,
    ActivePostShadow = 5,
}

internal sealed record CaseResult(
    int ShadowPercent,
    int ShadowKeyCount,
    string Mode,
    string SnapshotMode,
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long BaselinePayloadBytes,
    long ShadowAwarePayloadBytes,
    long ShadowReleasedPayloadBytes,
    long ExpectedReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    bool CandidateIsSubsetOfBaseline,
    bool ObserverEquivalenceVerified,
    int ObserverEquivalenceCheckCount,
    bool ObserverMinimalityVerified,
    int UnwitnessedRequiredVersionCount,
    int ReleasedVersionCount,
    int ParentFallbackHops,
    int LocalShadowStops);

internal sealed record FanoutCaseResult(
    int BranchCount,
    int ShadowPercent,
    int ShadowKeyCount,
    string Mode,
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long BaselinePayloadBytes,
    long ShadowAwarePayloadBytes,
    long ShadowReleasedPayloadBytes,
    long ExpectedReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    bool CandidateIsSubsetOfBaseline,
    bool ObserverEquivalenceVerified,
    int ObserverEquivalenceCheckCount,
    bool ObserverMinimalityVerified,
    int UnwitnessedRequiredVersionCount,
    int ReleasedVersionCount,
    int ParentFallbackHops);

internal sealed record NestedCaseResult(
    int Depth,
    int ShadowPercent,
    int ShadowKeyCount,
    int BaselineVersionCount,
    int ShadowAwareVersionCount,
    long BaselinePayloadBytes,
    long ShadowAwarePayloadBytes,
    long ShadowReleasedPayloadBytes,
    long ExpectedReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    bool CandidateIsSubsetOfBaseline,
    bool ObserverEquivalenceVerified,
    int ObserverEquivalenceCheckCount,
    bool ObserverMinimalityVerified,
    int UnwitnessedRequiredVersionCount,
    int ReleasedVersionCount,
    int ParentFallbackHops);

internal sealed record MixedCaseResult(
    int Seed,
    int BranchCount,
    int ShadowPercent,
    int ShadowOperations,
    int OverwriteCount,
    int TombstoneCount,
    long BaselinePayloadBytes,
    long ShadowAwarePayloadBytes,
    long ShadowReleasedPayloadBytes,
    long ExpectedReleasedPayloadBytes,
    double ShadowAwareReclamationRatio,
    bool CandidateIsSubsetOfBaseline,
    bool ObserverEquivalenceVerified,
    int ObserverEquivalenceCheckCount,
    bool ObserverMinimalityVerified,
    int UnwitnessedRequiredVersionCount);

internal sealed record PhysicalCaseResult(
    int BranchCount,
    int ShadowPercent,
    int ShadowKeyCount,
    string Mode,
    int BaselineGcReclaimedVersions,
    int CandidateGcReclaimedVersions,
    long CandidateShadowReleasedPayloadBytes,
    double CandidateLogicalReclamationRatio,
    double CandidateSerializedReclamationRatio,
    double BaselineGcMilliseconds,
    double CandidateGcMilliseconds,
    double CandidateProjectionAnalysisMilliseconds,
    double CandidateProjectionConstructionMilliseconds,
    double CandidateCoreProjectionMilliseconds,
    double CandidateObserverVerificationMilliseconds,
    double BaselineCompactionMilliseconds,
    double CandidateCompactionMilliseconds,
    long BaselineCheckpointLogicalBytes,
    long CandidateCheckpointLogicalBytes,
    long CheckpointLogicalReductionBytes,
    long BaselineCheckpointAllocatedBytes,
    long CandidateCheckpointAllocatedBytes,
    long CheckpointAllocatedReductionBytes,
    long BaselineTotalAllocatedBytes,
    long CandidateTotalAllocatedBytes,
    long TotalAllocatedReductionBytes,
    long BaselineCompactionBytesReclaimed,
    long CandidateCompactionBytesReclaimed,
    bool AllocationMeasurementExact,
    bool ObserverStateEqualAfterRestart);

internal sealed record PilotResult(
    string Pilot,
    string MainCommitUnderTest,
    int BaseKeyCount,
    int ValueBytes,
    int CaseCount,
    int FanoutCaseCount,
    int NestedCaseCount,
    int PhysicalCaseCount,
    int PhysicalObserverMismatchCount,
    int PhysicalAllocationIncompleteCount,
    int MixedCaseCount,
    int CandidateSubsetFailures,
    int ExpectedReleaseMismatches,
    int PreShadowSafetyFailures,
    int ObserverEquivalenceFailures,
    int ObserverMinimalityFailures,
    double MaximumReclamationRatio,
    double MedianReclamationRatio,
    long MaximumReleasedPayloadBytes,
    IReadOnlyList<CaseResult> Cases,
    IReadOnlyList<FanoutCaseResult> FanoutCases,
    IReadOnlyList<NestedCaseResult> NestedCases,
    IReadOnlyList<PhysicalCaseResult> PhysicalCases,
    IReadOnlyList<MixedCaseResult> MixedCases);
