using System.Buffers.Binary;
using System.Text.Json;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;

var options = Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

var fractions = new[] { 0, 25, 50, 75, 100 };
var modes = new[] { ShadowMode.Overwrite, ShadowMode.Tombstone };
var snapshots = new[] { SnapshotMode.None, SnapshotMode.PreShadow, SnapshotMode.PostShadow };
var cases = new List<CaseResult>();
var fanoutCases = new List<FanoutCaseResult>();
var ordinal = 0;

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

var eligible = cases.Where(item => item.SnapshotMode != SnapshotMode.PreShadow.ToString() && item.ShadowPercent > 0).ToArray();
var allRatios = eligible.Select(item => item.ShadowAwareReclamationRatio)
    .Concat(fanoutCases.Select(item => item.ShadowAwareReclamationRatio))
    .ToArray();
var result = new PilotResult(
    Pilot: "A1-SHADOW",
    MainCommitUnderTest: options.MainCommit,
    BaseKeyCount: options.BaseKeyCount,
    ValueBytes: options.ValueBytes,
    CaseCount: cases.Count,
    FanoutCaseCount: fanoutCases.Count,
    CandidateSubsetFailures: cases.Count(item => !item.CandidateIsSubsetOfBaseline)
        + fanoutCases.Count(item => !item.CandidateIsSubsetOfBaseline),
    ExpectedReleaseMismatches: cases.Count(item => item.ShadowReleasedPayloadBytes != item.ExpectedReleasedPayloadBytes)
        + fanoutCases.Count(item => item.ShadowReleasedPayloadBytes != item.ExpectedReleasedPayloadBytes),
    PreShadowSafetyFailures: cases.Count(item => item.SnapshotMode == SnapshotMode.PreShadow.ToString() && item.ShadowReleasedPayloadBytes != 0),
    ObserverEquivalenceFailures: cases.Count(item => !item.ObserverEquivalenceVerified)
        + fanoutCases.Count(item => !item.ObserverEquivalenceVerified),
    ObserverMinimalityFailures: cases.Count(item => !item.ObserverMinimalityVerified)
        + fanoutCases.Count(item => !item.ObserverMinimalityVerified),
    MaximumReclamationRatio: allRatios.Length == 0 ? 1d : allRatios.Max(),
    MedianReclamationRatio: Median(allRatios),
    MaximumReleasedPayloadBytes: Math.Max(
        cases.Max(item => item.ShadowReleasedPayloadBytes),
        fanoutCases.Max(item => item.ShadowReleasedPayloadBytes)),
    Cases: cases,
    FanoutCases: fanoutCases);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
File.WriteAllText(Path.Combine(options.OutputDirectory, "a1-shadow-result.json"), JsonSerializer.Serialize(result, jsonOptions));

var pass = result.CandidateSubsetFailures == 0
    && result.ExpectedReleaseMismatches == 0
    && result.PreShadowSafetyFailures == 0
    && result.ObserverEquivalenceFailures == 0
    && result.ObserverMinimalityFailures == 0;
Console.WriteLine(
    $"A1-SHADOW {(pass ? "PASS" : "FAIL")} cases={result.CaseCount} " +
    $"median-SAR={result.MedianReclamationRatio:F3}x max-SAR={result.MaximumReclamationRatio:F3}x " +
    $"max-release={result.MaximumReleasedPayloadBytes}B output={options.OutputDirectory}");
return pass ? 0 : 1;

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
        try
        {
            if (snapshotMode == SnapshotMode.PreShadow)
            {
                retainedSnapshot = branch.CreateSnapshot("pre-shadow");
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
            var expectedRelease = snapshotMode == SnapshotMode.PreShadow
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
        Console.Error.WriteLine("Usage: <base-key-count:8..4096> <value-bytes:1..1048576> [output-directory] [main-commit]");
        Environment.Exit(2);
    }

    var output = args.Length >= 3
        ? Path.GetFullPath(args[2])
        : Path.Combine(Environment.CurrentDirectory, "artifacts", "a1-shadow", Guid.NewGuid().ToString("N"));
    var commit = args.Length >= 4 ? args[3] : "5fa3d3835c42e929cef14ab90288e04b9e5c113b";
    return new Options(baseKeyCount, valueBytes, output, commit);
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

internal sealed record Options(int BaseKeyCount, int ValueBytes, string OutputDirectory, string MainCommit);

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

internal sealed record PilotResult(
    string Pilot,
    string MainCommitUnderTest,
    int BaseKeyCount,
    int ValueBytes,
    int CaseCount,
    int FanoutCaseCount,
    int CandidateSubsetFailures,
    int ExpectedReleaseMismatches,
    int PreShadowSafetyFailures,
    int ObserverEquivalenceFailures,
    int ObserverMinimalityFailures,
    double MaximumReclamationRatio,
    double MedianReclamationRatio,
    long MaximumReleasedPayloadBytes,
    IReadOnlyList<CaseResult> Cases,
    IReadOnlyList<FanoutCaseResult> FanoutCases);
