using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
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

    public static int WriteHoldoutPlans(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: --write-holdout-plans <sealed-plan-directory> <output-directory>");
            return 2;
        }

        try
        {
            var planDirectory = Path.GetFullPath(args[0]);
            var outputDirectory = Path.GetFullPath(args[1]);
            var (publicationPlan, publicationHash) = ReadAndVerifyPlan(planDirectory);
            var execution = ShadowRetentionHoldoutExecutionPlan.Create(publicationPlan);
            execution.ValidateAgainst(publicationPlan);
            var executionArtifact = ShadowRetentionHoldoutExecutionPlanWriter.Write(outputDirectory, execution);
            var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publicationPlan, execution);
            analysis.ValidateAgainst(publicationPlan, execution);
            var analysisArtifact = ShadowRetentionHoldoutAnalysisPlanWriter.Write(outputDirectory, analysis);

            Console.WriteLine(
                $"A1-HOLDOUT-PLANS SEALED A={execution.HoldoutARunCount} B={execution.HoldoutBRunCount} " +
                $"publication={publicationHash} execution={executionArtifact.Sha256} " +
                $"analysis={analysisArtifact.Sha256} output={outputDirectory}");
            Console.WriteLine("No Holdout-A or Holdout-B trial was executed by this command.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"A1-HOLDOUT-PLANS FAIL: {exception}");
            return 1;
        }
    }

    public static int PrepareHoldout(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine(
                "Usage: --prepare-holdout <sealed-plan-directory> <output-directory> <machine-block-id> <expected-main-base-commit>");
            return 2;
        }

        try
        {
            var planDirectory = Path.GetFullPath(args[0]);
            var outputDirectory = Path.GetFullPath(args[1]);
            var machineBlockId = args[2];
            var expectedMainBaseCommit = args[3].ToLowerInvariant();
            var repository = ResolveGitRepositoryIdentity(expectedMainBaseCommit);
            if (IsPathWithin(outputDirectory, repository.RepositoryRoot))
            {
                throw new InvalidOperationException(
                    "Holdout registration/output must live outside the Git working tree so evidence artifacts cannot dirty the sealed source identity.");
            }

            var (publicationPlan, publicationHash) = ReadAndVerifyPlan(planDirectory);
            var execution = ShadowRetentionHoldoutExecutionPlan.Create(publicationPlan);
            execution.ValidateAgainst(publicationPlan);
            var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publicationPlan, execution);
            analysis.ValidateAgainst(publicationPlan, execution);
            var binaryArtifacts = CaptureBinaryArtifacts();
            var registration = new ShadowRetentionHoldoutRegistration
            {
                FormatVersion = ShadowRetentionHoldoutRegistration.CurrentFormatVersion,
                CandidateId = publicationPlan.CandidateId,
                PublicationPlanSha256 = publicationHash,
                HoldoutExecutionPlanSha256 = execution.ComputeCanonicalSha256(),
                HoldoutAnalysisPlanSha256 = analysis.ComputeCanonicalSha256(),
                ExpectedMainBaseCommit = expectedMainBaseCommit,
                SourceCommit = repository.SourceCommit,
                SourceTree = repository.SourceTree,
                SourceTreeClean = repository.SourceTreeClean,
                ExpectedMainBaseIsAncestor = repository.ExpectedMainBaseIsAncestor,
                MachineBlockId = machineBlockId,
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
                OsDescription = RuntimeInformation.OSDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                HoldoutARunCount = execution.HoldoutARunCount,
                HoldoutBRunCount = execution.HoldoutBRunCount,
                InitialPartition = ShadowRetentionHoldoutPartition.HoldoutA,
                HoldoutBSealedBeforeA = true,
                BinaryArtifacts = binaryArtifacts,
            };
            registration.ValidateAgainst(publicationPlan, execution, analysis);

            var registrationDirectory = Path.Combine(outputDirectory, "registration");
            Directory.CreateDirectory(registrationDirectory);
            var publicationArtifact = ShadowRetentionPublicationPlanWriter.Write(registrationDirectory, publicationPlan);
            var executionArtifact = ShadowRetentionHoldoutExecutionPlanWriter.Write(registrationDirectory, execution);
            var analysisArtifact = ShadowRetentionHoldoutAnalysisPlanWriter.Write(registrationDirectory, analysis);
            var registrationArtifact = ShadowRetentionHoldoutRegistrationWriter.Write(registrationDirectory, registration);

            Console.WriteLine(
                $"A1-HOLDOUT PREPARED A={execution.HoldoutARunCount} B={execution.HoldoutBRunCount} " +
                $"publication={publicationArtifact.Sha256} execution={executionArtifact.Sha256} " +
                $"analysis={analysisArtifact.Sha256} registration={registrationArtifact.Sha256} " +
                $"source={repository.SourceCommit} tree={repository.SourceTree} machine={machineBlockId} output={outputDirectory}");
            Console.WriteLine("Holdout-B was sealed before A. No Holdout-A or Holdout-B trial was executed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"A1-HOLDOUT PREPARE FAIL: {exception}");
            return 1;
        }
    }

    public static int RunHoldout(string[] args, ShadowRetentionHoldoutPartition partition)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                partition == ShadowRetentionHoldoutPartition.HoldoutA
                    ? "Usage: --run-holdout-a <prepared-holdout-directory> <machine-block-id>"
                    : "Usage: --run-holdout-b <prepared-holdout-directory> <machine-block-id>");
            return 2;
        }

        var preparedDirectory = Path.GetFullPath(args[0]);
        var machineBlockId = args[1];
        try
        {
            var context = ReadAndVerifyPreparedHoldout(preparedDirectory, machineBlockId);
            var invalidationPath = Path.Combine(
                preparedDirectory,
                "invalidation",
                ShadowRetentionHoldoutInvalidationWriter.FileName);
            if (partition == ShadowRetentionHoldoutPartition.HoldoutA)
            {
                var bDirectory = Path.Combine(preparedDirectory, "holdout-b");
                if ((Directory.Exists(bDirectory) && Directory.EnumerateFileSystemEntries(bDirectory).Any())
                    || File.Exists(invalidationPath))
                {
                    throw new InvalidOperationException(
                        "Holdout-A cannot run after Holdout-B output exists or Holdout-A has been invalidated.");
                }
            }
            else
            {
                var invalidation = ReadAndVerifyInvalidation(preparedDirectory, context.Registration);
                invalidation.ValidateAgainst(context.Registration);
                var aResultPath = Path.Combine(preparedDirectory, "holdout-a", "holdout-a-result.json");
                if (File.Exists(aResultPath))
                {
                    var aResult = JsonSerializer.Deserialize<HoldoutPartitionResult>(File.ReadAllText(aResultPath), ReadOptions)
                        ?? throw new InvalidOperationException("Could not deserialize Holdout-A aggregate result.");
                    if (aResult.Complete && aResult.FailureCount == 0)
                    {
                        throw new InvalidOperationException(
                            "Holdout-B is forbidden because Holdout-A completed successfully; weak or unfavorable effect size is not an invalidation reason.");
                    }
                }
            }

            return ExecuteHoldoutPartition(context, preparedDirectory, partition);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"A1-{partition.ToString().ToUpperInvariant()} FAIL: {exception}");
            return 1;
        }
    }

    private static int ExecuteHoldoutPartition(
        PreparedHoldoutContext context,
        string preparedDirectory,
        ShadowRetentionHoldoutPartition partition)
    {
        var partitionName = partition == ShadowRetentionHoldoutPartition.HoldoutA ? "holdout-a" : "holdout-b";
        var partitionDirectory = Path.Combine(preparedDirectory, partitionName);
        Directory.CreateDirectory(partitionDirectory);
        var aggregatePath = Path.Combine(partitionDirectory, partitionName + "-result.json");
        if (File.Exists(aggregatePath))
        {
            var existing = JsonSerializer.Deserialize<HoldoutPartitionResult>(File.ReadAllText(aggregatePath), ReadOptions)
                ?? throw new InvalidOperationException($"Could not deserialize existing {partition} aggregate result.");
            if (!string.Equals(existing.RegistrationSha256, context.RegistrationHash, StringComparison.Ordinal)
                || existing.Partition != partition)
            {
                throw new InvalidOperationException($"Existing {partition} aggregate result belongs to a different registration.");
            }
            Console.WriteLine(
                $"A1-{partition.ToString().ToUpperInvariant()} EXISTING complete={existing.Complete} " +
                $"trials={existing.ExecutedTrialCount}/{existing.PlannedTrialCount} failures={existing.FailureCount} output={partitionDirectory}");
            return existing.Complete && existing.FailureCount == 0 ? 0 : 1;
        }

        var trials = context.Execution.Runs
            .Where(run => run.Partition == partition)
            .OrderBy(run => run.TrialOrder)
            .ToArray();
        var executions = new List<HoldoutTrialExecution>(trials.Length);
        var failureCount = 0;
        foreach (var trial in trials)
        {
            var runDirectory = Path.Combine(
                partitionDirectory,
                "runs",
                $"trial-{trial.TrialOrder:D4}-{trial.CaseId}-seed-{trial.Seed}-rep-{trial.ProcessRepetition:D2}");
            Directory.CreateDirectory(runDirectory);
            var resultPath = Path.Combine(runDirectory, "publication-case-result.json");
            if (File.Exists(resultPath))
            {
                var resumed = ReadAndValidateHoldoutCaseResult(trial, resultPath);
                executions.Add(ToHoldoutExecution(trial, resultPath, resumed, "RESUMED: verified immutable existing result artifact."));
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(runDirectory).Any())
            {
                throw new InvalidOperationException(
                    $"{partition} run directory is non-empty without a complete immutable result for '{trial.RunId}'. Resume is refused until the interrupted directory is audited.");
            }

            var childArgs = BuildHoldoutChildArguments(context.Publication, trial, runDirectory);
            ChildResult child;
            try
            {
                child = RunChild(childArgs);
            }
            catch (Exception exception) when (partition == ShadowRetentionHoldoutPartition.HoldoutA)
            {
                WriteHoldoutAInvalidation(
                    context,
                    preparedDirectory,
                    trial,
                    ShadowRetentionHoldoutInvalidationCategory.InfrastructureFailure,
                    "Child process infrastructure failure: " + exception.Message,
                    exception.ToString());
                throw;
            }

            if (child.ExitCode != 0)
            {
                failureCount++;
                var evidence = $"exit={child.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{child.StandardOutput}" +
                    $"{Environment.NewLine}stderr:{Environment.NewLine}{child.StandardError}";
                if (partition == ShadowRetentionHoldoutPartition.HoldoutA)
                {
                    var category = child.StandardError.Contains("invariant failed", StringComparison.OrdinalIgnoreCase)
                        ? ShadowRetentionHoldoutInvalidationCategory.CorrectnessFailure
                        : ShadowRetentionHoldoutInvalidationCategory.InfrastructureFailure;
                    WriteHoldoutAInvalidation(
                        context,
                        preparedDirectory,
                        trial,
                        category,
                        "Observed child-process failure during preregistered Holdout-A run.",
                        evidence);
                }

                executions.Add(new HoldoutTrialExecution(
                    trial.RunId,
                    trial.TrialOrder,
                    trial.CaseId,
                    trial.Seed,
                    trial.ProcessRepetition,
                    child.ExitCode,
                    null,
                    null,
                    null,
                    null,
                    null,
                    child.StandardOutput,
                    child.StandardError));
                break;
            }

            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException(
                    $"{partition} child succeeded without immutable result artifact for '{trial.RunId}'.");
            }
            var result = ReadAndValidateHoldoutCaseResult(trial, resultPath);
            executions.Add(ToHoldoutExecution(trial, resultPath, result, child.StandardOutput, child.StandardError));
        }

        var complete = executions.Count == trials.Length && failureCount == 0;
        var summaries = BuildHoldoutCaseSummaries(executions);
        var aggregate = new HoldoutPartitionResult(
            Pilot: "A1-SHADOW-" + partition.ToString().ToUpperInvariant(),
            RegistrationSha256: context.RegistrationHash,
            PublicationPlanSha256: context.PublicationHash,
            ExecutionPlanSha256: context.ExecutionHash,
            AnalysisPlanSha256: context.AnalysisHash,
            Partition: partition,
            PlannedTrialCount: trials.Length,
            ExecutedTrialCount: executions.Count,
            FailureCount: failureCount,
            Complete: complete,
            PartitionInvalidated: partition == ShadowRetentionHoldoutPartition.HoldoutA
                && File.Exists(Path.Combine(preparedDirectory, "invalidation", ShadowRetentionHoldoutInvalidationWriter.FileName)),
            Cases: summaries,
            Runs: executions);
        WriteCreateNew(aggregatePath, JsonSerializer.Serialize(aggregate, JsonOptions) + Environment.NewLine);

        Console.WriteLine(
            $"A1-{partition.ToString().ToUpperInvariant()} {(complete ? "PASS" : "FAIL")} " +
            $"trials={executions.Count}/{trials.Length} failures={failureCount} registration={context.RegistrationHash} output={partitionDirectory}");
        return complete ? 0 : 1;
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

    private static IReadOnlyList<string> BuildHoldoutChildArguments(
        ShadowRetentionPublicationPlan publicationPlan,
        ShadowRetentionHoldoutRunSpec trial,
        string runDirectory)
        =>
        [
            "--publication-case",
            trial.TopologyKind,
            trial.KeyCount.ToString(CultureInfo.InvariantCulture),
            trial.BranchCountOrDepth.ToString(CultureInfo.InvariantCulture),
            trial.ShadowFraction.ToString("R", CultureInfo.InvariantCulture),
            trial.TombstoneFraction.ToString("R", CultureInfo.InvariantCulture),
            publicationPlan.ValueBytes.ToString(CultureInfo.InvariantCulture),
            trial.Seed.ToString(CultureInfo.InvariantCulture),
            runDirectory,
        ];

    private static PublicationCaseResult ReadAndValidateHoldoutCaseResult(
        ShadowRetentionHoldoutRunSpec trial,
        string resultPath)
    {
        var result = JsonSerializer.Deserialize<PublicationCaseResult>(File.ReadAllText(resultPath), ReadOptions)
            ?? throw new InvalidOperationException(
                $"Could not deserialize existing holdout publication-case result for run '{trial.RunId}'.");
        ValidateCaseIdentity(trial, result);
        return result;
    }

    private static HoldoutTrialExecution ToHoldoutExecution(
        ShadowRetentionHoldoutRunSpec trial,
        string resultPath,
        PublicationCaseResult result,
        string standardOutput,
        string standardError = "")
        => new(
            trial.RunId,
            trial.TrialOrder,
            trial.CaseId,
            trial.Seed,
            trial.ProcessRepetition,
            0,
            Sha256(File.ReadAllBytes(resultPath)),
            result.MeasuredReclamationRatio,
            result.MeasuredReleasedPayloadBytes,
            result.VerifiedProjectionMilliseconds,
            result.ThreadAllocatedBytes,
            standardOutput,
            standardError);

    private static HoldoutCaseSummary[] BuildHoldoutCaseSummaries(IReadOnlyList<HoldoutTrialExecution> executions)
        => executions
            .Where(run => run.ExitCode == 0 && run.MeasuredReclamationRatio.HasValue)
            .GroupBy(run => run.CaseId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new HoldoutCaseSummary(
                group.Key,
                group.Count(),
                Quantiles(group.Select(item => item.MeasuredReclamationRatio!.Value)),
                Quantiles(group.Select(item => (double)item.MeasuredReleasedPayloadBytes!.Value)),
                Quantiles(group.Select(item => item.VerifiedProjectionMilliseconds!.Value)),
                Quantiles(group.Select(item => (double)item.ThreadAllocatedBytes!.Value))))
            .ToArray();

    private static HoldoutMetricQuantiles Quantiles(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return new HoldoutMetricQuantiles(0d, 0d, 0d);
        }
        return new HoldoutMetricQuantiles(
            QuantileLinear(ordered, 0.05d),
            QuantileLinear(ordered, 0.50d),
            QuantileLinear(ordered, 0.95d));
    }

    private static double QuantileLinear(double[] ordered, double probability)
    {
        var index = (ordered.Length - 1) * probability;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return ordered[lower];
        }
        var fraction = index - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private static void WriteHoldoutAInvalidation(
        PreparedHoldoutContext context,
        string preparedDirectory,
        ShadowRetentionHoldoutRunSpec trial,
        ShadowRetentionHoldoutInvalidationCategory category,
        string reason,
        string evidence)
    {
        var evidenceDirectory = Path.Combine(preparedDirectory, "invalidation");
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "a1-shadow-holdout-a-failure-evidence.txt");
        WriteCreateNew(evidencePath, evidence + Environment.NewLine);
        var invalidation = new ShadowRetentionHoldoutInvalidation
        {
            FormatVersion = ShadowRetentionHoldoutInvalidation.CurrentFormatVersion,
            CandidateId = context.Publication.CandidateId,
            RegistrationSha256 = context.RegistrationHash,
            HoldoutExecutionPlanSha256 = context.ExecutionHash,
            InvalidatedPartition = ShadowRetentionHoldoutPartition.HoldoutA,
            Category = category,
            FailedRunId = trial.RunId,
            FailureEvidenceSha256 = Sha256(File.ReadAllBytes(evidencePath)),
            Reason = reason,
        };
        invalidation.ValidateAgainst(context.Registration);
        _ = ShadowRetentionHoldoutInvalidationWriter.Write(evidenceDirectory, invalidation);
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
            || !string.Equals(result.Pilot, "A1-SHADOW-PUBLICATION-CASE", StringComparison.Ordinal)
            || !result.FlatExactBaselineVerified
            || !result.CandidateSubsetVerified
            || !result.ObserverEquivalenceVerified
            || !result.ObserverMinimalityVerified
            || result.ExpectedReleasedPayloadBytes != result.MeasuredReleasedPayloadBytes
            || Math.Abs(result.ExpectedReclamationRatio - result.MeasuredReclamationRatio) > 1e-12
            || !double.IsFinite(result.MeasuredReclamationRatio)
            || !double.IsFinite(result.VerifiedProjectionMilliseconds)
            || result.VerifiedProjectionMilliseconds < 0d
            || result.ThreadAllocatedBytes < 0
            || result.RealizedShadowKeyCount < 0
            || result.RealizedShadowKeyCount > result.KeyCount
            || result.RealizedTombstoneKeyCount < 0
            || result.RealizedTombstoneKeyCount > result.RealizedShadowKeyCount)
        {
            throw new InvalidOperationException(
                $"Publication-case result identity or correctness gates do not match sealed run '{trial.RunId}'.");
        }
    }

    private static GitRepositoryIdentity ResolveGitRepositoryIdentity(string expectedMainBaseCommit)
    {
        if ((expectedMainBaseCommit.Length is not (40 or 64))
            || expectedMainBaseCommit.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException("Expected main-base commit must be a Git object id.");
        }

        var repositoryRoot = RunGit(["rev-parse", "--show-toplevel"], requireSuccess: true).StandardOutput.Trim();
        var sourceCommit = RunGit(["rev-parse", "HEAD"], requireSuccess: true).StandardOutput.Trim().ToLowerInvariant();
        var sourceTree = RunGit(["rev-parse", "HEAD^{tree}"], requireSuccess: true).StandardOutput.Trim().ToLowerInvariant();
        var status = RunGit(["status", "--porcelain=v1", "--untracked-files=all"], requireSuccess: true).StandardOutput;
        var ancestor = RunGit(["merge-base", "--is-ancestor", expectedMainBaseCommit, "HEAD"], requireSuccess: false);
        var clean = string.IsNullOrWhiteSpace(status);
        if (!clean)
        {
            throw new InvalidOperationException("Holdout source tree is not clean; commit or remove every tracked/untracked source change before sealing.");
        }
        if (ancestor.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Declared main base {expectedMainBaseCommit} is not an ancestor of source commit {sourceCommit}; do not open Holdout-A on an uncomposed source tree.");
        }

        return new GitRepositoryIdentity(
            Path.GetFullPath(repositoryRoot),
            sourceCommit,
            sourceTree,
            SourceTreeClean: true,
            ExpectedMainBaseIsAncestor: true);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ShadowRetentionBinaryArtifactIdentity> CaptureBinaryArtifacts()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var files = Directory.EnumerateFiles(baseDirectory, "ChronicleDB*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("Could not resolve ChronicleDB holdout binary artifacts.");
        }

        return Array.AsReadOnly(files.Select(path =>
        {
            var info = new FileInfo(path);
            return new ShadowRetentionBinaryArtifactIdentity
            {
                Name = info.Name,
                LengthBytes = info.Length,
                Sha256 = Sha256(File.ReadAllBytes(path)),
            };
        }).ToArray());
    }

    private static bool IsPathWithin(string candidatePath, string parentPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parentPath), Path.GetFullPath(candidatePath));
        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }

    private static GitCommandResult RunGit(IReadOnlyList<string> arguments, bool requireSuccess)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git while sealing holdout source identity.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var result = new GitCommandResult(process.ExitCode, stdout, stderr);
        if (requireSuccess && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git source-identity command failed ({string.Join(' ', arguments)}): {result.StandardError.Trim()}");
        }
        return result;
    }

    private static void ValidateCaseIdentity(
        ShadowRetentionHoldoutRunSpec trial,
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
            || !string.Equals(result.Pilot, "A1-SHADOW-PUBLICATION-CASE", StringComparison.Ordinal)
            || !result.FlatExactBaselineVerified
            || !result.CandidateSubsetVerified
            || !result.ObserverEquivalenceVerified
            || !result.ObserverMinimalityVerified
            || result.ExpectedReleasedPayloadBytes != result.MeasuredReleasedPayloadBytes
            || Math.Abs(result.ExpectedReclamationRatio - result.MeasuredReclamationRatio) > 1e-12
            || !double.IsFinite(result.MeasuredReclamationRatio)
            || !double.IsFinite(result.VerifiedProjectionMilliseconds)
            || result.VerifiedProjectionMilliseconds < 0d
            || result.ThreadAllocatedBytes < 0
            || result.RealizedShadowKeyCount < 0
            || result.RealizedShadowKeyCount > result.KeyCount
            || result.RealizedTombstoneKeyCount < 0
            || result.RealizedTombstoneKeyCount > result.RealizedShadowKeyCount)
        {
            throw new InvalidOperationException(
                $"Holdout publication-case result identity or correctness gates do not match sealed run '{trial.RunId}'.");
        }
    }

    private static PreparedHoldoutContext ReadAndVerifyPreparedHoldout(
        string preparedDirectory,
        string machineBlockId)
    {
        var registrationDirectory = Path.Combine(preparedDirectory, "registration");
        var (publication, publicationHash) = ReadAndVerifyPlan(registrationDirectory);

        var executionPath = Path.Combine(registrationDirectory, ShadowRetentionHoldoutExecutionPlanWriter.PlanFileName);
        var executionHashPath = Path.Combine(registrationDirectory, ShadowRetentionHoldoutExecutionPlanWriter.PlanHashFileName);
        var analysisPath = Path.Combine(registrationDirectory, ShadowRetentionHoldoutAnalysisPlanWriter.PlanFileName);
        var analysisHashPath = Path.Combine(registrationDirectory, ShadowRetentionHoldoutAnalysisPlanWriter.PlanHashFileName);
        var registrationPath = Path.Combine(registrationDirectory, ShadowRetentionHoldoutRegistrationWriter.RegistrationFileName);
        var registrationHashPath = Path.Combine(registrationDirectory, ShadowRetentionHoldoutRegistrationWriter.RegistrationHashFileName);
        foreach (var path in new[]
        {
            executionPath,
            executionHashPath,
            analysisPath,
            analysisHashPath,
            registrationPath,
            registrationHashPath,
        })
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Prepared holdout artifact is missing: {path}");
            }
        }

        var execution = JsonSerializer.Deserialize<ShadowRetentionHoldoutExecutionPlan>(File.ReadAllText(executionPath), ReadOptions)
            ?? throw new InvalidOperationException("Could not deserialize holdout execution plan.");
        execution.ValidateAgainst(publication);
        var executionHash = execution.ComputeCanonicalSha256();
        if (!string.Equals(executionHash, File.ReadAllText(executionHashPath).Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout execution-plan hash sidecar mismatch.");
        }

        var analysis = JsonSerializer.Deserialize<ShadowRetentionHoldoutAnalysisPlan>(File.ReadAllText(analysisPath), ReadOptions)
            ?? throw new InvalidOperationException("Could not deserialize holdout analysis plan.");
        analysis.ValidateAgainst(publication, execution);
        var analysisHash = analysis.ComputeCanonicalSha256();
        if (!string.Equals(analysisHash, File.ReadAllText(analysisHashPath).Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout analysis-plan hash sidecar mismatch.");
        }

        var registration = JsonSerializer.Deserialize<ShadowRetentionHoldoutRegistration>(File.ReadAllText(registrationPath), ReadOptions)
            ?? throw new InvalidOperationException("Could not deserialize holdout registration.");
        registration.ValidateAgainst(publication, execution, analysis);
        var registrationHash = registration.ComputeCanonicalSha256();
        if (!string.Equals(registrationHash, File.ReadAllText(registrationHashPath).Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout registration hash sidecar mismatch.");
        }

        var currentSource = ResolveGitRepositoryIdentity(registration.ExpectedMainBaseCommit);
        var currentBinaries = CaptureBinaryArtifacts();
        if (!string.Equals(machineBlockId, registration.MachineBlockId, StringComparison.Ordinal)
            || !string.Equals(currentSource.SourceCommit, registration.SourceCommit, StringComparison.Ordinal)
            || !string.Equals(currentSource.SourceTree, registration.SourceTree, StringComparison.Ordinal)
            || !string.Equals(RuntimeInformation.FrameworkDescription, registration.FrameworkDescription, StringComparison.Ordinal)
            || !string.Equals(RuntimeInformation.OSDescription, registration.OsDescription, StringComparison.Ordinal)
            || !string.Equals(RuntimeInformation.ProcessArchitecture.ToString(), registration.ProcessArchitecture, StringComparison.Ordinal)
            || !string.Equals(RuntimeInformation.OSArchitecture.ToString(), registration.OsArchitecture, StringComparison.Ordinal)
            || !currentBinaries.SequenceEqual(registration.BinaryArtifacts))
        {
            throw new InvalidOperationException(
                "Current source, binary, runtime or machine-block identity differs from the sealed holdout registration.");
        }

        return new PreparedHoldoutContext(
            publication,
            publicationHash,
            execution,
            executionHash,
            analysis,
            analysisHash,
            registration,
            registrationHash);
    }

    private static ShadowRetentionHoldoutInvalidation ReadAndVerifyInvalidation(
        string preparedDirectory,
        ShadowRetentionHoldoutRegistration registration)
    {
        var directory = Path.Combine(preparedDirectory, "invalidation");
        var path = Path.Combine(directory, ShadowRetentionHoldoutInvalidationWriter.FileName);
        var hashPath = Path.Combine(directory, ShadowRetentionHoldoutInvalidationWriter.HashFileName);
        if (!File.Exists(path) || !File.Exists(hashPath))
        {
            throw new InvalidOperationException(
                "Holdout-B is sealed. A preregistered correctness/infrastructure Holdout-A invalidation artifact is required before B can execute.");
        }
        var invalidation = JsonSerializer.Deserialize<ShadowRetentionHoldoutInvalidation>(File.ReadAllText(path), ReadOptions)
            ?? throw new InvalidOperationException("Could not deserialize Holdout-A invalidation artifact.");
        invalidation.ValidateAgainst(registration);
        if (!string.Equals(invalidation.ComputeCanonicalSha256(), File.ReadAllText(hashPath).Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Holdout-A invalidation hash sidecar mismatch.");
        }
        return invalidation;
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

    private sealed record PreparedHoldoutContext(
        ShadowRetentionPublicationPlan Publication,
        string PublicationHash,
        ShadowRetentionHoldoutExecutionPlan Execution,
        string ExecutionHash,
        ShadowRetentionHoldoutAnalysisPlan Analysis,
        string AnalysisHash,
        ShadowRetentionHoldoutRegistration Registration,
        string RegistrationHash);

    private sealed record ChildResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record GitRepositoryIdentity(
        string RepositoryRoot,
        string SourceCommit,
        string SourceTree,
        bool SourceTreeClean,
        bool ExpectedMainBaseIsAncestor);
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

internal sealed record HoldoutMetricQuantiles(double P05, double P50, double P95);

internal sealed record HoldoutCaseSummary(
    string CaseId,
    int RunCount,
    HoldoutMetricQuantiles ReclamationRatio,
    HoldoutMetricQuantiles ReleasedPayloadBytes,
    HoldoutMetricQuantiles VerifiedProjectionMilliseconds,
    HoldoutMetricQuantiles ThreadAllocatedBytes);

internal sealed record HoldoutTrialExecution(
    string RunId,
    int TrialOrder,
    string CaseId,
    int Seed,
    int ProcessRepetition,
    int ExitCode,
    string? ResultSha256,
    double? MeasuredReclamationRatio,
    long? MeasuredReleasedPayloadBytes,
    double? VerifiedProjectionMilliseconds,
    long? ThreadAllocatedBytes,
    string StandardOutput,
    string StandardError);

internal sealed record HoldoutPartitionResult(
    string Pilot,
    string RegistrationSha256,
    string PublicationPlanSha256,
    string ExecutionPlanSha256,
    string AnalysisPlanSha256,
    ShadowRetentionHoldoutPartition Partition,
    int PlannedTrialCount,
    int ExecutedTrialCount,
    int FailureCount,
    bool Complete,
    bool PartitionInvalidated,
    IReadOnlyList<HoldoutCaseSummary> Cases,
    IReadOnlyList<HoldoutTrialExecution> Runs);

