using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Storage;

internal static partial class ResearchPilotRunner
{
    private static int RunRetentionSealedHoldout(string[] args)
    {
        if (args.Length < 11
            || !int.TryParse(args[0], out var holdoutASeedStart)
            || !int.TryParse(args[1], out var holdoutBSeedStart)
            || !int.TryParse(args[2], out var seedCount)
            || !int.TryParse(args[3], out var repetitions)
            || !int.TryParse(args[4], out var baseKeyCount)
            || !int.TryParse(args[5], out var valueBytes)
            || !int.TryParse(args[6], out var churnRounds)
            || !int.TryParse(args[7], out var hotKeyCount)
            || !int.TryParse(args[8], out var privateBytes)
            || !int.TryParse(args[9], out var readBudget)
            || string.IsNullOrWhiteSpace(args[10])
            || seedCount is < 1 or > 20
            || repetitions is < 2 or > 10
            || baseKeyCount < 16
            || valueBytes <= 0
            || churnRounds <= 0
            || hotKeyCount <= 0
            || hotKeyCount > baseKeyCount
            || privateBytes <= 0
            || readBudget < 1_000)
        {
            Console.Error.WriteLine(
                "Usage: pilot P1H <holdout-a-seed-start> <holdout-b-seed-start> <seed-count:1..20> " +
                "<repetitions:2..10> <base-key-count>=16 <value-bytes> <churn-rounds> <hot-key-count> " +
                "<private-bytes> <read-budget>=1000 <machine-block> [output-directory]");
            return 2;
        }

        var machineBlock = args[10];
        var outputDirectory = args.Length >= 12
            ? Path.GetFullPath(args[11])
            : Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "research-holdout",
                $"p1h-{holdoutASeedStart}-{holdoutBSeedStart}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var config = new RetentionHoldoutConfig(
                CandidateId: "A1",
                CandidateMode: "a1-observer-exact-v3",
                NoveltyCardVersion: "a1-observer-exact-v3",
                FailureModelVersion: "persistence-v1",
                HoldoutASeedStart: holdoutASeedStart,
                HoldoutBSeedStart: holdoutBSeedStart,
                SeedCount: seedCount,
                Repetitions: repetitions,
                BaseKeyCount: baseKeyCount,
                ValueBytes: valueBytes,
                ChurnRounds: churnRounds,
                HotKeyCount: hotKeyCount,
                PrivateBytes: privateBytes,
                ReadBudget: readBudget,
                MachineBlock: machineBlock);
            var configJson = JsonSerializer.Serialize(config, JsonOptions);
            var configHash = Sha256(Encoding.UTF8.GetBytes(configJson));
            var configPath = Path.Combine(outputDirectory, "candidate-config.json");
            WriteCreateNew(configPath, configJson + Environment.NewLine);

            var sealedAt = DateTimeOffset.UtcNow;
            var plans = new List<RetentionHoldoutRunPlan>(checked(seedCount * repetitions * 2));
            BuildHoldoutPartitionPlans(
                ResearchCampaignPartition.HoldoutA,
                holdoutASeedStart,
                config,
                configHash,
                sealedAt,
                plans);
            BuildHoldoutPartitionPlans(
                ResearchCampaignPartition.HoldoutB,
                holdoutBSeedStart,
                config,
                configHash,
                sealedAt,
                plans);

            foreach (var plan in plans)
            {
                var manifestDirectory = Path.Combine(
                    outputDirectory,
                    "registration",
                    plan.Partition.ToString(),
                    $"trial-{plan.TrialOrder:D3}-seed-{plan.Seed}-rep-{plan.Repetition:D2}");
                var artifact = new ResearchArtifactWriter(manifestDirectory).WriteManifest(plan.Manifest);
                if (!string.Equals(artifact.Sha256, plan.ManifestSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Precomputed holdout manifest hash changed during sealing.");
                }
            }

            var registration = new ResearchCampaignRegistration
            {
                FormatVersion = ResearchCampaignRegistration.CurrentFormatVersion,
                CandidateId = config.CandidateId,
                CandidateConfigHash = configHash,
                NoveltyCardVersion = config.NoveltyCardVersion,
                FailureModelVersion = config.FailureModelVersion,
                UtcSealedAt = sealedAt,
                Runs = plans.Select(plan => new ResearchCampaignRunRegistration(
                    plan.Manifest.ExperimentId,
                    plan.Partition,
                    plan.Seed,
                    plan.Seed,
                    plan.Seed,
                    plan.Repetition,
                    machineBlock,
                    plan.TrialOrder,
                    plan.ManifestSha256)).ToArray(),
            };
            var registrationArtifact = new ResearchArtifactWriter(Path.Combine(outputDirectory, "registration"))
                .WriteCampaignRegistration(registration);

            // Holdout-B is deliberately sealed before the first Holdout-A result exists.
            var holdoutAPlans = plans
                .Where(plan => plan.Partition == ResearchCampaignPartition.HoldoutA)
                .OrderBy(plan => plan.TrialOrder)
                .ToArray();
            var runs = new List<RetentionHoldoutExecution>(holdoutAPlans.Length);
            foreach (var plan in holdoutAPlans)
            {
                var runDirectory = Path.Combine(
                    outputDirectory,
                    "holdout-a-results",
                    $"trial-{plan.TrialOrder:D3}-seed-{plan.Seed}-rep-{plan.Repetition:D2}");
                Directory.CreateDirectory(runDirectory);
                var child = RunP1IChild(
                    plan.Seed,
                    baseKeyCount,
                    valueBytes,
                    churnRounds,
                    hotKeyCount,
                    privateBytes,
                    readBudget,
                    runDirectory);
                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"P1H Holdout-A child failed seed={plan.Seed} repetition={plan.Repetition}: {child.StandardError}");
                }

                var resultPath = Path.Combine(runDirectory, "p1i-result.json");
                using var resultDocument = JsonDocument.Parse(File.ReadAllText(resultPath));
                var root = resultDocument.RootElement;
                if (root.GetProperty("seed").GetInt32() != plan.Seed
                    || root.GetProperty("baseKeyCount").GetInt32() != baseKeyCount
                    || root.GetProperty("valueBytes").GetInt32() != valueBytes
                    || root.GetProperty("churnRounds").GetInt32() != churnRounds
                    || root.GetProperty("hotKeyCount").GetInt32() != hotKeyCount
                    || root.GetProperty("privateBytes").GetInt32() != privateBytes
                    || root.GetProperty("readBudget").GetInt32() != readBudget)
                {
                    throw new InvalidOperationException("Holdout result identity does not match the sealed manifest/configuration.");
                }

                runs.Add(new RetentionHoldoutExecution(
                    ExperimentId: plan.Manifest.ExperimentId,
                    ManifestSha256: plan.ManifestSha256,
                    ResultSha256: Sha256(File.ReadAllBytes(resultPath)),
                    Seed: plan.Seed,
                    Repetition: plan.Repetition,
                    TrialOrder: plan.TrialOrder,
                    P99InterferenceRatio: root.GetProperty("p99InterferenceRatio").GetDouble(),
                    P95InterferenceRatio: root.GetProperty("p95InterferenceRatio").GetDouble(),
                    ExactMarginalPayloadBytes: root.GetProperty("exactMarginalPayloadBytes").GetInt64(),
                    CoarseRootInducedPayloadBytes: root.GetProperty("coarseRootInducedPayloadBytes").GetInt64(),
                    CompactionBytesRewritten: root.GetProperty("compactionBytesRewritten").GetInt64(),
                    CompactionBytesReclaimed: root.GetProperty("compactionBytesReclaimed").GetInt64(),
                    AllocationMeasurementExact: root.GetProperty("allocationMeasurementExact").GetBoolean()));
            }

            var orderedP99 = runs.Select(run => run.P99InterferenceRatio).Order().ToArray();
            var execution = new RetentionHoldoutResult(
                Pilot: "P1H",
                CandidateId: config.CandidateId,
                CandidateConfigHash: configHash,
                CampaignRegistrationSha256: registrationArtifact.Sha256,
                HoldoutARunCount: runs.Count,
                HoldoutBSealedRunCount: plans.Count(plan => plan.Partition == ResearchCampaignPartition.HoldoutB),
                HoldoutBExecuted: false,
                MedianP99InterferenceRatio: Median(orderedP99),
                P95P99InterferenceRatio: Percentile(orderedP99, 0.95),
                EveryRunValid: runs.All(run => run.AllocationMeasurementExact
                    && run.ExactMarginalPayloadBytes > 0
                    && run.CompactionBytesReclaimed >= 0
                    && double.IsFinite(run.P99InterferenceRatio)),
                Runs: runs);
            var resultJson = JsonSerializer.Serialize(execution, JsonOptions);
            var resultPathFinal = Path.Combine(outputDirectory, "p1h-holdout-a-result.json");
            WriteCreateNew(resultPathFinal, resultJson + Environment.NewLine);

            var pass = execution.EveryRunValid
                && execution.HoldoutARunCount == seedCount * repetitions
                && execution.HoldoutBSealedRunCount == seedCount * repetitions
                && !execution.HoldoutBExecuted;
            Console.WriteLine(
                $"P1H {(pass ? "PASS" : "FAIL")} A-runs={execution.HoldoutARunCount} " +
                $"B-sealed={execution.HoldoutBSealedRunCount} B-executed={execution.HoldoutBExecuted} " +
                $"p99-median={execution.MedianP99InterferenceRatio:F2}x p99-p95={execution.P95P99InterferenceRatio:F2}x " +
                $"registration={registrationArtifact.Sha256} output={outputDirectory}");
            return pass ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1H FAIL: {exception}");
            return 1;
        }
    }

    private static void BuildHoldoutPartitionPlans(
        ResearchCampaignPartition partition,
        int seedStart,
        RetentionHoldoutConfig config,
        string configHash,
        DateTimeOffset sealedAt,
        ICollection<RetentionHoldoutRunPlan> destination)
    {
        var trials = new List<(int Seed, int Repetition)>(checked(config.SeedCount * config.Repetitions));
        for (var seedOffset = 0; seedOffset < config.SeedCount; seedOffset++)
        {
            var seed = checked(seedStart + seedOffset);
            for (var repetition = 0; repetition < config.Repetitions; repetition++)
            {
                trials.Add((seed, repetition));
            }
        }

        var shuffle = new Random(StableCampaignShuffleSeed(
            seedStart,
            config.SeedCount,
            config.Repetitions,
            config.BaseKeyCount,
            config.ValueBytes,
            config.ChurnRounds,
            config.HotKeyCount,
            config.PrivateBytes,
            config.ReadBudget,
            (int)partition));
        for (var index = trials.Count - 1; index > 0; index--)
        {
            var other = shuffle.Next(index + 1);
            (trials[index], trials[other]) = (trials[other], trials[index]);
        }

        for (var trialOrder = 0; trialOrder < trials.Count; trialOrder++)
        {
            var (seed, repetition) = trials[trialOrder];
            var experimentId = StableExperimentId(configHash, partition, seed, repetition, trialOrder);
            var manifest = CreateHoldoutManifest(
                experimentId,
                config,
                configHash,
                seed,
                repetition,
                trialOrder,
                sealedAt);
            destination.Add(new RetentionHoldoutRunPlan(
                partition,
                seed,
                repetition,
                trialOrder,
                manifest,
                manifest.ComputeCanonicalSha256()));
        }
    }

    private static ExperimentManifest CreateHoldoutManifest(
        Guid experimentId,
        RetentionHoldoutConfig config,
        string configHash,
        int seed,
        int repetition,
        int trialOrder,
        DateTimeOffset campaignStartedAt)
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
        var drive = new DriveInfo(root);
        return new ExperimentManifest
        {
            ExperimentId = experimentId,
            ManifestFormatVersion = ExperimentManifest.CurrentFormatVersion,
            ResearchTraceFormatVersion = ResearchTraceSerializer.CurrentFormatVersion,
            ChronicleVersion = "v1.1-research",
            GitCommit = ResolveResearchGitCommit(),
            BuildConfiguration = "Release",
            MachineId = Environment.MachineName,
            Cpu = RuntimeInformation.ProcessArchitecture.ToString(),
            MemoryBytes = Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            Disk = drive.Name,
            FileSystem = string.IsNullOrWhiteSpace(drive.DriveFormat) ? "unknown" : drive.DriveFormat,
            OperatingSystem = RuntimeInformation.OSDescription,
            DotNetVersion = Environment.Version.ToString(),
            PageSize = StorageOptions.DefaultPageSize,
            KeySize = sizeof(int),
            ValueSize = config.ValueBytes,
            WorkloadSeed = seed,
            CrashPlanSeed = seed,
            MutationSeed = seed,
            ProcessRepetition = repetition,
            MachineBlock = config.MachineBlock,
            TrialOrder = trialOrder,
            WorkloadFamily = "P1I-RetentionInterference",
            DurationMilliseconds = 0,
            CacheState = "fresh-process",
            BranchCount = 1,
            BranchDepth = 1,
            Fanout = 1,
            BranchAgeMilliseconds = config.ChurnRounds,
            Divergence = (double)config.HotKeyCount / config.BaseKeyCount,
            SnapshotCount = 0,
            SnapshotAgeMilliseconds = 0,
            GcMode = "retain-recent-0",
            CompactionMode = "copy-publish",
            DurabilityMode = "durable",
            CandidateMode = config.CandidateMode,
            CandidateConfigHash = configHash,
            NoveltyCardVersion = config.NoveltyCardVersion,
            FailureModelVersion = config.FailureModelVersion,
            TelemetryMode = ResearchTelemetryMode.Disabled,
            UtcStartedAt = campaignStartedAt,
        };
    }

    private static Guid StableExperimentId(
        string configHash,
        ResearchCampaignPartition partition,
        int seed,
        int repetition,
        int trialOrder)
    {
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{configHash}|{partition}|{seed}|{repetition}|{trialOrder}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string ResolveResearchGitCommit()
    {
        var configured = Environment.GetEnvironmentVariable("CHRONICLE_GIT_COMMIT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                if (process.WaitForExit(milliseconds: 2_000)
                    && process.ExitCode == 0
                    && !string.IsNullOrWhiteSpace(output))
                {
                    return output.Trim();
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Publication validation can reject an unknown commit if git is unavailable.
        }
        return "unknown";
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteCreateNew(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed record RetentionHoldoutConfig(
        string CandidateId,
        string CandidateMode,
        string NoveltyCardVersion,
        string FailureModelVersion,
        int HoldoutASeedStart,
        int HoldoutBSeedStart,
        int SeedCount,
        int Repetitions,
        int BaseKeyCount,
        int ValueBytes,
        int ChurnRounds,
        int HotKeyCount,
        int PrivateBytes,
        int ReadBudget,
        string MachineBlock);

    private sealed record RetentionHoldoutRunPlan(
        ResearchCampaignPartition Partition,
        int Seed,
        int Repetition,
        int TrialOrder,
        ExperimentManifest Manifest,
        string ManifestSha256);

    private sealed record RetentionHoldoutExecution(
        Guid ExperimentId,
        string ManifestSha256,
        string ResultSha256,
        int Seed,
        int Repetition,
        int TrialOrder,
        double P99InterferenceRatio,
        double P95InterferenceRatio,
        long ExactMarginalPayloadBytes,
        long CoarseRootInducedPayloadBytes,
        long CompactionBytesRewritten,
        long CompactionBytesReclaimed,
        bool AllocationMeasurementExact);

    private sealed record RetentionHoldoutResult(
        string Pilot,
        string CandidateId,
        string CandidateConfigHash,
        string CampaignRegistrationSha256,
        int HoldoutARunCount,
        int HoldoutBSealedRunCount,
        bool HoldoutBExecuted,
        double MedianP99InterferenceRatio,
        double P95P99InterferenceRatio,
        bool EveryRunValid,
        IReadOnlyList<RetentionHoldoutExecution> Runs);
}
