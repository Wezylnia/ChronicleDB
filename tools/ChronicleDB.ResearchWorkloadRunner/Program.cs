using System.Diagnostics;
using System.Runtime.InteropServices;
using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Transactions.Faults;

if (args.Length > 0 && args[0].Equals("crash", StringComparison.OrdinalIgnoreCase))
{
    return await RunCrashParentAsync(args[1..]);
}

if (args.Length > 0 && args[0].Equals("child", StringComparison.OrdinalIgnoreCase))
{
    return RunCrashChild(args[1..]);
}

if (args.Length < 3
    || !TryParseFamily(args[0], out var family)
    || !int.TryParse(args[1], out var seed)
    || !int.TryParse(args[2], out var operationCount)
    || operationCount < 0)
{
    Console.Error.WriteLine(
        "Usage: ChronicleDB.ResearchWorkloadRunner <S0|S1|S2|S3|S4|S6> <seed> <operation-count> [output-directory]");
    return 2;
}

if (family is not (ResearchWorkloadFamily.S0Control
    or ResearchWorkloadFamily.S1OldThinBranch
    or ResearchWorkloadFamily.S2OverlappingRoots
    or ResearchWorkloadFamily.S3DeepInheritance
    or ResearchWorkloadFamily.S4WideIndependentHistories
    or ResearchWorkloadFamily.S6ErasureConflict))
{
    Console.Error.WriteLine("This baseline runner currently supports only S0-S3; S4-S7 need family-specific execution semantics.");
    return 2;
}

var outputDirectory = args.Length >= 4
    ? Path.GetFullPath(args[3])
    : Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "research",
        $"{family}-{seed}-{Guid.NewGuid():N}");
Directory.CreateDirectory(outputDirectory);
var databaseDirectory = Path.Combine(outputDirectory, "database");
var artifactDirectory = Path.Combine(outputDirectory, "artifacts");

var operations = DeterministicResearchWorkloadGenerator.Generate(family, seed, operationCount);
var profile = ResearchWorkloadProfiler.Analyze(operations);
var crashPlan = ResearchCrashPlanFactory.Create(operations, seed: seed);
var manifest = CreateManifest(family, seed, profile);
var sink = new TraceResearchEventSink();
var session = new ResearchExperimentSession(
    new ResearchArtifactWriter(artifactDirectory),
    manifest,
    operations,
    crashPlan);
var branches = new Dictionary<int, ChronicleBranch>();
var snapshots = new List<IDisposable>();

try
{
    using var database = ChronicleDatabase.Open(databaseDirectory, researchEventSink: sink);
    foreach (var operation in operations)
    {
        Execute(database, branches, snapshots, operation);
    }

    var events = sink.Snapshot();
    ResearchTraceValidator.Validate(events);
    var traceArtifact = session.Complete(events);
    Console.WriteLine(
        $"PASS family={family} seed={seed} operations={operationCount} events={events.Count} " +
        $"manifest={session.ManifestArtifact.Sha256} workload={session.WorkloadArtifact.Sha256} " +
        $"crash-plan={session.CrashPlanArtifact!.Sha256} trace={traceArtifact.Sha256} " +
        $"output={outputDirectory}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL family={family} seed={seed}: {exception.Message}");
    return 1;
}
finally
{
    foreach (var snapshot in snapshots)
    {
        snapshot.Dispose();
    }

    foreach (var branch in branches.Values)
    {
        branch.Dispose();
    }
}

static void Execute(
    ChronicleDatabase database,
    IDictionary<int, ChronicleBranch> branches,
    ICollection<IDisposable> snapshots,
    ResearchWorkloadOperation operation,
    IDictionary<int, Guid>? branchIds = null)
{
    switch (operation.Kind)
    {
        case ResearchWorkloadOperationKind.CreateBranch:
            if (branches.ContainsKey(operation.HistorySlot))
            {
                throw new InvalidOperationException($"History slot {operation.HistorySlot} was branched more than once.");
            }

            var name = $"research-branch-{operation.HistorySlot}";
            var branch = operation.ParentHistorySlot == 0
                ? database.CreateBranch(name)
                : branches[operation.ParentHistorySlot].CreateBranch(name);
            branches.Add(operation.HistorySlot, branch);
            branchIds?.Add(operation.HistorySlot, branch.BranchId);
            break;
        case ResearchWorkloadOperationKind.Put:
            ResolveBranch(branches, operation.HistorySlot)?.Put(Key(operation), Value(operation));
            if (operation.HistorySlot == 0)
            {
                database.Put(Key(operation), Value(operation));
            }
            break;
        case ResearchWorkloadOperationKind.Delete:
            if (operation.HistorySlot == 0)
            {
                database.Delete(Key(operation));
            }
            else
            {
                branches[operation.HistorySlot].Delete(Key(operation));
            }
            break;
        case ResearchWorkloadOperationKind.Read:
            if (operation.HistorySlot == 0)
            {
                _ = database.TryGet(Key(operation), out _);
            }
            else
            {
                _ = branches[operation.HistorySlot].TryGet(Key(operation), out _);
            }
            break;
        case ResearchWorkloadOperationKind.CreateSnapshot:
            if (operation.HistorySlot == 0)
            {
                snapshots.Add(database.CreateSnapshot($"research-snapshot-{operation.Step}"));
            }
            else
            {
                snapshots.Add(branches[operation.HistorySlot].CreateSnapshot($"research-snapshot-{operation.Step}"));
            }
            break;
        case ResearchWorkloadOperationKind.GarbageCollect:
            _ = database.RunGarbageCollection();
            break;
        case ResearchWorkloadOperationKind.Compact:
            _ = database.RunCompaction();
            break;
        case ResearchWorkloadOperationKind.Crash:
        case ResearchWorkloadOperationKind.Recover:
            throw new NotSupportedException("Crash/recover execution requires a process harness and is not simulated in this runner.");
        default:
            throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Unknown workload operation kind.");
    }
}

static ChronicleBranch? ResolveBranch(
    IDictionary<int, ChronicleBranch> branches,
    int historySlot)
    => historySlot == 0 ? null : branches[historySlot];

static byte[] Key(ResearchWorkloadOperation operation)
    => [checked((byte)(operation.KeyId % 256))];

static byte[] Value(ResearchWorkloadOperation operation)
{
    var length = Math.Max(1, operation.ValueSize);
    var value = new byte[length];
    for (var index = 0; index < value.Length; index++)
    {
        value[index] = checked((byte)((operation.Step + operation.KeyId + index) % 256));
    }

    return value;
}

static bool TryParseFamily(string value, out ResearchWorkloadFamily family)
{
    switch (value.ToUpperInvariant())
    {
        case "S0":
        case "S0CONTROL":
            family = ResearchWorkloadFamily.S0Control;
            return true;
        case "S1":
        case "S1OLDTHINBRANCH":
            family = ResearchWorkloadFamily.S1OldThinBranch;
            return true;
        case "S2":
        case "S2OVERLAPPINGROOTS":
            family = ResearchWorkloadFamily.S2OverlappingRoots;
            return true;
        case "S3":
        case "S3DEEPINHERITANCE":
            family = ResearchWorkloadFamily.S3DeepInheritance;
            return true;
        case "S4":
        case "S4WIDEINDEPENDENTHISTORIES":
            family = ResearchWorkloadFamily.S4WideIndependentHistories;
            return true;
        case "S5":
        case "S5RECOVERYHEAVY":
            family = ResearchWorkloadFamily.S5RecoveryHeavy;
            return true;
        case "S6":
        case "S6ERASURECONFLICT":
            family = ResearchWorkloadFamily.S6ErasureConflict;
            return true;
        case "S7":
        case "S7MIXEDADVERSARIALSOAK":
            family = ResearchWorkloadFamily.S7MixedAdversarialSoak;
            return true;
        default:
            family = default;
            return false;
    }
}

static ExperimentManifest CreateManifest(
    ResearchWorkloadFamily family,
    int seed,
    ResearchWorkloadProfile profile)
{
    var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
    var drive = new DriveInfo(root);
    return new ExperimentManifest
    {
        ExperimentId = Guid.NewGuid(),
        ManifestFormatVersion = 1,
        ResearchTraceFormatVersion = ResearchTraceSerializer.CurrentFormatVersion,
        ChronicleVersion = "v1.1-research",
        GitCommit = Environment.GetEnvironmentVariable("CHRONICLE_GIT_COMMIT") ?? "unknown",
        BuildConfiguration = "Release",
        MachineId = Environment.MachineName,
        Cpu = RuntimeInformation.ProcessArchitecture.ToString(),
        MemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Disk = drive.Name,
        FileSystem = drive.DriveFormat,
        OperatingSystem = RuntimeInformation.OSDescription,
        DotNetVersion = Environment.Version.ToString(),
        PageSize = 4096,
        KeySize = 1,
        ValueSize = Math.Max(1, profile.MaximumValueSize),
        WorkloadSeed = seed,
        CrashPlanSeed = seed,
        MutationSeed = seed,
        ProcessRepetition = 1,
        MachineBlock = Environment.MachineName,
        TrialOrder = 0,
        WorkloadFamily = family.ToString(),
        DurationMilliseconds = 0,
        BranchCount = profile.BranchCount,
        BranchDepth = profile.MaximumBranchDepth,
        Fanout = profile.MaximumFanout,
        BranchAgeMilliseconds = 0,
        Divergence = 0,
        SnapshotCount = profile.SnapshotCount,
        SnapshotAgeMilliseconds = 0,
        GcMode = "baseline",
        CompactionMode = "baseline",
        DurabilityMode = "durable",
        CandidateMode = "v1.0-baseline",
        CandidateConfigHash = "baseline-default",
        NoveltyCardVersion = "v1.1",
        FailureModelVersion = "persistence-v1",
        TelemetryMode = ResearchTelemetryMode.Trace,
        UtcStartedAt = DateTimeOffset.UtcNow,
    };
}

static async Task<int> RunCrashParentAsync(string[] args)
{
    if (args.Length < 3
        || !TryParseFamily(args[0], out var family)
        || !int.TryParse(args[1], out var seed)
        || !int.TryParse(args[2], out var operationCount)
        || operationCount < 0)
    {
        Console.Error.WriteLine(
            "Usage: crash <S5|S7> <seed> <operation-count> [output-directory]");
        return 2;
    }

    if (family is not (ResearchWorkloadFamily.S5RecoveryHeavy or ResearchWorkloadFamily.S7MixedAdversarialSoak))
    {
        Console.Error.WriteLine("Crash mode is reserved for S5 and S7 workload families.");
        return 2;
    }

    var outputDirectory = args.Length >= 4
        ? Path.GetFullPath(args[3])
        : Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "research-crash",
            $"{family}-{seed}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outputDirectory);
    var databaseDirectory = Path.Combine(outputDirectory, "database");
    var artifactDirectory = Path.Combine(outputDirectory, "artifacts");

    var operations = DeterministicResearchWorkloadGenerator.Generate(family, seed, operationCount);
    var profile = ResearchWorkloadProfiler.Analyze(operations);
    var crashPlan = ResearchCrashPlanFactory.Create(operations, seed);
    if (crashPlan.Injections.Count == 0)
    {
        Console.Error.WriteLine("Crash mode requires at least one Crash operation in the generated workload.");
        return 2;
    }

    var injection = crashPlan.Injections[0];
    var session = new ResearchExperimentSession(
        new ResearchArtifactWriter(artifactDirectory),
        CreateManifest(family, seed, profile),
        operations,
        crashPlan);

    using var process = StartCrashChild(
        databaseDirectory,
        family,
        seed,
        operationCount,
        injection);
    await process.WaitForExitAsync();
    if (process.ExitCode == 0)
    {
        Console.Error.WriteLine("Crash child exited normally; the configured fault point was not reached.");
        return 1;
    }

    var sink = new TraceResearchEventSink();
    using (var recovered = ChronicleDatabase.Open(databaseDirectory, researchEventSink: sink))
    {
        _ = recovered.GetDiagnostics();
    }

    var recoveryEvents = sink.Snapshot();
    ResearchTraceValidator.Validate(recoveryEvents);
    var traceArtifact = session.Complete(recoveryEvents);
    Console.WriteLine(
        $"PASS crash-recovery family={family} seed={seed} step={injection.OperationStep} " +
        $"fault={injection.FaultPoint} child-exit={process.ExitCode} recovery-events={recoveryEvents.Count} " +
        $"crash-plan={session.CrashPlanArtifact!.Sha256} recovery-trace={traceArtifact.Sha256} output={outputDirectory}");
    return 0;
}

static Process StartCrashChild(
    string databaseDirectory,
    ResearchWorkloadFamily family,
    int seed,
    int operationCount,
    ResearchCrashInjection injection)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
    if (processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(typeof(CrashInjector).Assembly.Location);
    }

    startInfo.ArgumentList.Add("child");
    startInfo.ArgumentList.Add(databaseDirectory);
    startInfo.ArgumentList.Add(family.ToString());
    startInfo.ArgumentList.Add(seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(operationCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(injection.OperationStep.ToString(System.Globalization.CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(injection.FaultPoint);
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start research crash child process.");
}

static int RunCrashChild(string[] args)
{
    if (args.Length != 6
        || !TryParseFamily(args[1], out var family)
        || !int.TryParse(args[2], out var seed)
        || !int.TryParse(args[3], out var operationCount)
        || !int.TryParse(args[4], out var crashStep)
        || !Enum.TryParse<TransactionFaultPoint>(args[5], ignoreCase: true, out var faultPoint))
    {
        Console.Error.WriteLine("Usage: child <database-directory> <family> <seed> <operation-count> <crash-step> <fault-point>");
        return 2;
    }

    var operations = DeterministicResearchWorkloadGenerator.Generate(family, seed, operationCount);
    var crashOperation = operations.SingleOrDefault(operation => operation.Step == crashStep);
    if (crashOperation is null || crashOperation.Kind != ResearchWorkloadOperationKind.Crash)
    {
        Console.Error.WriteLine("Crash step does not identify a Crash workload operation.");
        return 2;
    }

    var branches = new Dictionary<int, ChronicleBranch>();
    var branchIds = new Dictionary<int, Guid>();
    var snapshots = new List<IDisposable>();
    var database = ChronicleDatabase.Open(args[0]);
    try
    {
        foreach (var operation in operations.Where(operation => operation.Step < crashStep))
        {
            Execute(database, branches, snapshots, operation, branchIds);
        }

        foreach (var branch in branches.Values)
        {
            branch.Dispose();
        }
        branches.Clear();
        database.Dispose();

        database = ChronicleDatabase.Open(
            args[0],
            faultInjector: new CrashInjector(faultPoint));
        foreach (var (slot, branchId) in branchIds)
        {
            branches.Add(slot, database.OpenBranch(branchId));
        }

        ExecuteCrashOperation(database, branches, crashOperation);
        return 0;
    }
    finally
    {
        foreach (var snapshot in snapshots)
        {
            snapshot.Dispose();
        }

        foreach (var branch in branches.Values)
        {
            branch.Dispose();
        }

        database.Dispose();
    }
}

static void ExecuteCrashOperation(
    ChronicleDatabase database,
    IReadOnlyDictionary<int, ChronicleBranch> branches,
    ResearchWorkloadOperation operation)
{
    if (operation.HistorySlot == 0)
    {
        database.Put(Key(operation), Value(operation));
    }
    else
    {
        branches[operation.HistorySlot].Put(Key(operation), Value(operation));
    }
}

file sealed class CrashInjector(TransactionFaultPoint target) : ITransactionFaultInjector
{
    public void Hit(TransactionFaultPoint point)
    {
        if (point == target)
        {
            Environment.FailFast($"Injected research crash at {point}.");
        }
    }
}
