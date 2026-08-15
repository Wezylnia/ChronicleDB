using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.BranchCheck;

public sealed record ExternalEvidenceManifest(
    int SchemaVersion,
    string Repository,
    string ResearchHeadSha,
    string ImportedIntoMainSha,
    IReadOnlyList<ExternalEvidenceArtifact> Artifacts);

public sealed record ExternalEvidenceArtifact(
    string Key,
    string Kind,
    long ArtifactId,
    long WorkflowRunId,
    string SourceHeadSha,
    string Sha256,
    string Archive,
    string EvidenceClass,
    IReadOnlyList<string> RequiredEntries,
    string ExternalIdentity,
    string? SelectionNote = null);

public sealed record ExternalEvidenceArtifactResult(
    string Key,
    string Kind,
    long ArtifactId,
    string ExternalIdentity,
    bool DigestValid,
    bool StructureValid,
    bool SemanticValid,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Errors)
{
    public bool Passed => DigestValid && StructureValid && SemanticValid && Errors.Count == 0;
}

public sealed record ExternalEvidenceBundleReport(
    string Repository,
    string ResearchHeadSha,
    string ImportedIntoMainSha,
    IReadOnlyList<ExternalEvidenceArtifactResult> Artifacts)
{
    public bool Passed => Artifacts.Count > 0 && Artifacts.All(static artifact => artifact.Passed);

    public int ExternalSystemCount
        => Artifacts
            .Select(static artifact => artifact.Key.StartsWith("matrixone-", StringComparison.Ordinal)
                ? "matrixone"
                : artifact.Key.StartsWith("dolt-", StringComparison.Ordinal)
                    ? "dolt"
                    : artifact.Key.StartsWith("slatedb-", StringComparison.Ordinal)
                        ? "slatedb"
                        : null)
            .Where(static key => key is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();
}

/// <summary>
/// Validates immutable GitHub Actions evidence imported from the frozen BranchCheck research branch.
/// The gate verifies archive identity before checking the semantic polarity used by the paper.
/// </summary>
public static class ExternalEvidenceBundleValidator
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ExternalEvidenceBundleReport Validate(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string absoluteManifestPath = Path.GetFullPath(manifestPath);
        string baseDirectory = Path.GetDirectoryName(absoluteManifestPath)
            ?? throw new InvalidDataException("External evidence manifest must have a parent directory.");
        ExternalEvidenceManifest manifest = JsonSerializer.Deserialize<ExternalEvidenceManifest>(
            File.ReadAllText(absoluteManifestPath, Encoding.UTF8),
            ManifestJsonOptions)
            ?? throw new InvalidDataException("External evidence manifest is empty or invalid.");

        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported external evidence schema version {manifest.SchemaVersion}.");
        }
        if (manifest.Artifacts.Count == 0)
        {
            throw new InvalidDataException("External evidence manifest contains no artifacts.");
        }
        if (manifest.Artifacts.Select(static artifact => artifact.Key).Distinct(StringComparer.Ordinal).Count()
            != manifest.Artifacts.Count)
        {
            throw new InvalidDataException("External evidence manifest contains duplicate artifact keys.");
        }
        if (manifest.Artifacts.Select(static artifact => artifact.ArtifactId).Distinct().Count()
            != manifest.Artifacts.Count)
        {
            throw new InvalidDataException("External evidence manifest contains duplicate GitHub artifact IDs.");
        }

        ExternalEvidenceArtifactResult[] results = manifest.Artifacts
            .Select(artifact => ValidateArtifact(baseDirectory, artifact))
            .ToArray();

        return new ExternalEvidenceBundleReport(
            manifest.Repository,
            manifest.ResearchHeadSha,
            manifest.ImportedIntoMainSha,
            results);
    }

    private static ExternalEvidenceArtifactResult ValidateArtifact(
        string baseDirectory,
        ExternalEvidenceArtifact artifact)
    {
        List<string> findings = [];
        List<string> errors = [];
        bool digestValid = false;
        bool structureValid = false;
        bool semanticValid = false;

        try
        {
            string archivePath = ResolveArchivePath(baseDirectory, artifact.Archive);
            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException("External evidence archive was not found.", archivePath);
            }

            string actualDigest = ComputeSha256(archivePath);
            digestValid = string.Equals(actualDigest, artifact.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!digestValid)
            {
                errors.Add($"SHA-256 mismatch: expected={artifact.Sha256}, actual={actualDigest}.");
                return Result();
            }
            findings.Add($"archive-sha256={actualDigest}");

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string[] entryNames = archive.Entries.Select(static entry => entry.FullName).ToArray();
            string[] missingEntries = artifact.RequiredEntries
                .Where(required => !entryNames.Contains(required, StringComparer.Ordinal))
                .ToArray();
            structureValid = missingEntries.Length == 0;
            if (!structureValid)
            {
                errors.Add("Missing required archive entries: " + string.Join(", ", missingEntries));
                return Result();
            }
            findings.Add($"required-entries={artifact.RequiredEntries.Count}");

            ValidateSemantics(archive, artifact, findings);
            semanticValid = true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            errors.Add(exception.Message);
        }

        return Result();

        ExternalEvidenceArtifactResult Result()
            => new(
                artifact.Key,
                artifact.Kind,
                artifact.ArtifactId,
                artifact.ExternalIdentity,
                digestValid,
                structureValid,
                semanticValid,
                findings,
                errors);
    }

    private static void ValidateSemantics(
        ZipArchive archive,
        ExternalEvidenceArtifact artifact,
        List<string> findings)
    {
        switch (artifact.Kind)
        {
            case "MatrixOneLive":
                ValidateMatrixOne(archive, findings);
                break;
            case "MatrixOneV2Live":
                ValidateMatrixOneV2(archive, findings);
                break;
            case "SlateDbPaired":
                ValidateSlateDb(archive, findings);
                break;
            case "Dolt223Budget":
                ValidateDolt223Budget(archive, findings);
                break;
            case "DoltReleaseRepeat":
                ValidateDoltReleaseRepeat(archive, findings);
                break;
            case "DoltMainCausal":
                ValidateDoltMainCausal(archive, findings);
                break;
            case "DoltExpandedBudget":
                ValidateDoltExpandedBudget(archive, findings);
                break;
            case "SlateDbExpandedPaired":
                ValidateSlateDbExpanded(archive, findings);
                break;
            case "ExternalUnseededReplay":
                ValidateExternalUnseededReplay(archive, findings);
                break;
            case "DoltRobustness":
                ValidateDoltRobustness(archive, findings);
                break;
            default:
                throw new InvalidDataException($"Unknown external evidence kind '{artifact.Kind}'.");
        }
    }

    private static void ValidateMatrixOne(ZipArchive archive, List<string> findings)
    {
        using JsonDocument continuation = ReadJson(archive, "matrixone-continuation.json");
        RequireRelationStatus(continuation.RootElement, "BC.continuation-state", "Fail");
        RequireBaselineStatus(continuation.RootElement, "B1.creation-visible-state", "Detected");
        RequireBaselineStatus(continuation.RootElement, "B2.generic-state-differential", "Detected");
        RequireBoolean(continuation.RootElement, true, "Report", "BranchCheckDetected");
        RequireBoolean(continuation.RootElement, true, "AnyGenericBaselineDetected");
        RequireBoolean(continuation.RootElement, false, "StrictBranchCheckOnly");

        using JsonDocument identity = ReadJson(archive, "matrixone-identity.json");
        RequireRelationStatus(identity.RootElement, "BC.temporal-boundary", "Fail");
        RequireBaselineStatus(identity.RootElement, "B0.creation-values", "Pass");
        RequireBaselineStatus(identity.RootElement, "B2.generic-state-differential", "Pass");
        RequireString(identity.RootElement, "Pass", "BranchGrammarBaseline", "Status");
        RequireBoolean(identity.RootElement, false, "AnyGenericBaselineDetected");
        RequireBoolean(identity.RootElement, true, "StrictBranchCheckOnly");

        using JsonDocument budget = ReadJson(archive, "matrixone-trigger-budget.json");
        JsonElement report = RequireProperty(budget.RootElement, "Report");
        RequireBoolean(report, true, "ExactlyOneViolationRecipe");
        RequireBoolean(report, true, "GuidedRecipeIsViolation");
        JsonElement[] curve = RequireArray(report, "BudgetCurve");
        RequireDouble(curve[0], 0.2, "GenericDetectionRate");
        RequireDouble(curve[0], 1.0, "RelationGuidedDetectionRate");
        RequireDouble(curve[^1], 1.0, "GenericDetectionRate");
        RequireDouble(curve[^1], 1.0, "RelationGuidedDetectionRate");

        findings.Add("matrixone-continuation=generic-detectable-negative-control");
        findings.Add("matrixone-identity=B0/B2/B4-pass;BC-temporal-fail");
        findings.Add("matrixone-legacy-budget=0.20-vs-1.00@budget1;target-seeded-not-fair-rq3-evidence");
    }

    private static void ValidateMatrixOneV2(ZipArchive archive, List<string> findings)
    {
        using JsonDocument continuation = ReadJson(archive, "matrixone-continuation.json");
        RequireRelationStatus(continuation.RootElement, "BC.continuation-state", "Fail");
        RequireBaselineStatus(continuation.RootElement, "B1.creation-visible-state", "Detected");
        RequireBaselineStatus(continuation.RootElement, "B2.generic-state-differential", "Detected");
        RequireBoolean(continuation.RootElement, true, "Report", "BranchCheckDetected");
        RequireBoolean(continuation.RootElement, true, "AnyGenericBaselineDetected");
        RequireBoolean(continuation.RootElement, false, "StrictBranchCheckOnly");

        using JsonDocument identityReport = ReadJson(archive, "matrixone-identity.json");
        RequireRelationStatus(identityReport.RootElement, "BC.temporal-boundary", "Fail");
        RequireBaselineStatus(identityReport.RootElement, "B0.creation-values", "Pass");
        RequireBaselineStatus(identityReport.RootElement, "B2.generic-state-differential", "Pass");
        RequireString(identityReport.RootElement, "Pass", "BranchGrammarBaseline", "Status");
        RequireBoolean(identityReport.RootElement, false, "AnyGenericBaselineDetected");
        RequireBoolean(identityReport.RootElement, true, "StrictBranchCheckOnly");

        using JsonDocument budget = ReadJson(archive, "matrixone-trigger-budget.json");
        JsonElement report = RequireProperty(budget.RootElement, "Report");
        RequireInt32(report, 10, "CandidateCount");
        RequireInt32(report, 3, "IdentityRelevantRecipeCount");
        RequireInt32(report, 3, "ViolationRecipeCount");
        RequireBoolean(report, false, "AllViolationsInsideIdentityRelevantClass");
        RequireBoolean(report, true, "GuidedHasStrictAdvantageAtAnyBudget");
        RequireString(report, "1FA61958C7E97E5EC5BBC8F32D03D99BAAD902F5C360465A7594B8F053B52040", "CandidateSetFingerprint");
        JsonElement[] recipes = RequireArray(report, "Recipes");
        JsonElement[] curve = RequireArray(report, "BudgetCurve");
        if (recipes.Length != 10 || curve.Length != 10)
        {
            throw new InvalidDataException("MatrixOne v2 artifact does not contain the frozen ten-candidate grammar.");
        }

        string identity = ReadText(archive, "matrixone-image-pinned.txt");
        if (!identity.Contains("sha256:c920128b7e02622f27ddc12db32f8968d030c8eb3fd05bcccac47ca7d5b1c2b8", StringComparison.Ordinal)
            || !identity.Contains("Workflow run: 31898523617", StringComparison.Ordinal))
        {
            throw new InvalidDataException("MatrixOne v2 archive does not contain the pinned image and workflow identity.");
        }

        findings.Add("matrixone-v2-pinned-image=c920128b7e02622f27ddc12db32f8968d030c8eb3fd05bcccac47ca7d5b1c2b8");
        findings.Add("matrixone-v2-fair-grammar=10-candidate;fingerprint-validated-in-workflow");
        findings.Add("matrixone-v2-budget=0.30-vs-0.67@budget1;3/10-violations;generic-baseline-control-preserved");
    }

    private static void ValidateSlateDb(ZipArchive archive, List<string> findings)
    {
        using JsonDocument buggy = ReadJson(archive, "slatedb-buggy.json");
        RequireRelationStatus(buggy.RootElement, "BC.observer-dependency", "Fail");
        RequireString(buggy.RootElement, "Detected", "ObserverSmokeBaseline", "Status");
        RequireBoolean(buggy.RootElement, true, "AnyGenericBaselineDetected");
        RequireBoolean(buggy.RootElement, false, "StrictBranchCheckOnly");

        using JsonDocument fixedEvidence = ReadJson(archive, "slatedb-fixed.json");
        RequireRelationStatus(fixedEvidence.RootElement, "BC.observer-dependency", "Pass");
        RequireString(fixedEvidence.RootElement, "Pass", "ObserverSmokeBaseline", "Status");
        RequireBoolean(fixedEvidence.RootElement, false, "AnyGenericBaselineDetected");

        using JsonDocument buggyBudget = ReadJson(archive, "slatedb-buggy-budget.json");
        JsonElement buggyReport = RequireProperty(buggyBudget.RootElement, "Report");
        RequireInt32(buggyReport, 1, "ViolationCandidateCount");
        RequireBoolean(buggyReport, true, "GuidedCandidateIsViolation");

        using JsonDocument fixedBudget = ReadJson(archive, "slatedb-fixed-budget.json");
        JsonElement fixedReport = RequireProperty(fixedBudget.RootElement, "Report");
        RequireInt32(fixedReport, 0, "ViolationCandidateCount");
        RequireBoolean(fixedReport, false, "GuidedCandidateIsViolation");

        findings.Add("slatedb-paired=buggy-fail/fixed-pass");
        findings.Add("slatedb-B5=detects-buggy-version");
        findings.Add("slatedb-budget=regression-only;not-fair-search-evidence");
    }

    private static void ValidateDolt223Budget(ZipArchive archive, List<string> findings)
    {
        using JsonDocument budget = ReadJson(archive, "dolt-2.2.3-budget.json");
        JsonElement report = RequireProperty(budget.RootElement, "Report");
        RequireInt32(report, 3, "ViolationRecipeCount");
        RequireInt32(report, 3, "SequenceRelevantRecipeCount");
        RequireBoolean(report, true, "AllViolationsInsideSequenceRelevantClass");
        RequireBoolean(report, true, "GuidedHasStrictAdvantageAtAnyBudget");

        JsonElement[] recipes = RequireArray(report, "Recipes");
        RequireRecipe(recipes, "NoOp", sequenceRelevant: false, "Pass", "Pass");
        RequireRecipe(recipes, "FetchOnly", sequenceRelevant: true, "Fail", "Pass");
        RequireRecipe(recipes, "Pull", sequenceRelevant: true, "Fail", "Pass");
        RequireRecipe(recipes, "FetchMerge", sequenceRelevant: true, "Fail", "Pass");

        JsonElement[] curve = RequireArray(report, "BudgetCurve");
        RequireDouble(curve[0], 0.75, "GenericDetectionRate");
        RequireDouble(curve[0], 1.0, "GuidedDetectionRate");
        foreach (JsonElement point in curve.Skip(1))
        {
            RequireDouble(point, 1.0, "GenericDetectionRate");
            RequireDouble(point, 1.0, "GuidedDetectionRate");
        }

        findings.Add("dolt-2.2.3-budget=0.75-vs-1.00@budget1");
        findings.Add("dolt-2.2.3-B4=passes-all-four-recipes");
    }

    private static void ValidateDoltReleaseRepeat(ZipArchive archive, List<string> findings)
    {
        using JsonDocument summary = ReadJson(archive, "summary.json");
        JsonElement oldVersion = RequireProperty(summary.RootElement, "223");
        JsonElement newVersion = RequireProperty(summary.RootElement, "230");

        RequireInt32(oldVersion, 10, "runs");
        RequireDictionaryCount(oldVersion, "relation_status", "Pass", 10);
        RequireDictionaryCount(oldVersion, "outcomes", "Success", 10);
        RequireDictionaryCount(oldVersion, "generated_ids", "1", 10);

        RequireInt32(newVersion, 10, "runs");
        int passCount = RequireDictionaryCountAtLeast(newVersion, "relation_status", "Pass", 1);
        int failCount = RequireDictionaryCountAtLeast(newVersion, "relation_status", "Fail", 1);
        int contextCanceled = RequireContainingDictionaryCount(newVersion, "details", "context canceled");
        if (contextCanceled != failCount)
        {
            throw new InvalidDataException(
                $"Dolt 2.3.0 repeat failure signature mismatch: fails={failCount}, context-canceled={contextCanceled}.");
        }
        if (passCount + failCount != 10)
        {
            throw new InvalidDataException("Dolt 2.3.0 repeat relation-status counts do not total ten runs.");
        }

        findings.Add("dolt-2.2.3-repeat=10/10-pass");
        findings.Add($"dolt-2.3.0-repeat={passCount}/10-pass;{failCount}/10-context-canceled");
    }

    private static void ValidateDoltMainCausal(ZipArchive archive, List<string> findings)
    {
        using JsonDocument summary = ReadJson(archive, "dolt-main-repeat/summary.json");
        JsonElement unpatched = RequireProperty(summary.RootElement, "unpatched");
        JsonElement patched = RequireProperty(summary.RootElement, "patched");

        RequireInt32(unpatched, 20, "runs");
        int unpatchedPass = RequireDictionaryCountAtLeast(unpatched, "relation_status", "Pass", 1);
        int unpatchedFail = RequireDictionaryCountAtLeast(unpatched, "relation_status", "Fail", 1);
        int unpatchedContextCanceled = RequireContainingDictionaryCount(unpatched, "details", "context canceled");
        RequireDictionaryCount(unpatched, "clone_grammar", "Pass", 20);
        if (unpatchedContextCanceled != unpatchedFail || unpatchedPass + unpatchedFail != 20)
        {
            throw new InvalidDataException("Unpatched Dolt current-main causal counts are inconsistent.");
        }

        RequireInt32(patched, 20, "runs");
        RequireDictionaryCount(patched, "relation_status", "Pass", 20);
        RequireDictionaryCount(patched, "outcomes", "Success", 20);
        RequireDictionaryCount(patched, "generated_ids", "1", 20);
        RequireDictionaryCount(patched, "clone_grammar", "Pass", 20);

        string patchText = ReadText(archive, "dolt-main-causal-patch.diff");
        if (!patchText.Contains("ctx = context.Background()", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Dolt causal control does not contain the frozen context-lifetime line.");
        }

        findings.Add($"dolt-main-unpatched={unpatchedPass}/20-pass;{unpatchedFail}/20-context-canceled");
        findings.Add("dolt-main-causal-control=20/20-pass");
    }

    private static void ValidateDoltExpandedBudget(ZipArchive archive, List<string> findings)
    {
        foreach ((string entryName, string version, int expectedViolations, bool expectedAllRelevant) in new[]
        {
            ("dolt-expanded-223.json", "Dolt provider dolt version 2.2.3", 6, true),
            ("dolt-expanded-230.json", "Dolt provider dolt version 2.3.0", 10, false),
        })
        {
            using JsonDocument budget = ReadJson(archive, entryName);
            JsonElement report = RequireProperty(budget.RootElement, "Report");
            RequireStringPrefix(report, version, "BackendVersion");
            RequireString(report, "51C8F5284EF8E58C226F003ACA0D91701FBFEDB6B4086C25ECA8319D0F73E40B", "CandidateSetFingerprint");
            RequireInt32(report, 6, "SequenceRelevantRecipeCount");
            RequireInt32(report, expectedViolations, "ViolationRecipeCount");
            RequireBoolean(report, expectedAllRelevant, "AllViolationsInsideSequenceRelevantClass");
            JsonElement[] recipes = RequireArray(report, "Recipes");
            if (recipes.Length != 10 || recipes.Select(recipe => RequireString(recipe, "Recipe")).Distinct(StringComparer.Ordinal).Count() != 10)
            {
                throw new InvalidDataException($"Expanded Dolt artifact '{entryName}' does not contain ten unique recipes.");
            }
            JsonElement[] curve = RequireArray(report, "BudgetCurve");
            if (curve.Length != 10)
            {
                throw new InvalidDataException($"Expanded Dolt artifact '{entryName}' does not contain ten budget points.");
            }
        }

        findings.Add("dolt-expanded=10-candidate-grammar;2.2.3=6/10-violations;2.3.0=10/10-violations");
        findings.Add("dolt-expanded-fairness=semantic-class-prioritized;fingerprint-validated");
    }

    private static void ValidateSlateDbExpanded(ZipArchive archive, List<string> findings)
    {
        using JsonDocument buggy = ReadJson(archive, "slatedb-expanded-buggy.json");
        using JsonDocument fixedEvidence = ReadJson(archive, "slatedb-expanded-fixed.json");
        JsonElement buggyReport = RequireProperty(buggy.RootElement, "Report");
        JsonElement fixedReport = RequireProperty(fixedEvidence.RootElement, "Report");
        const string fingerprint = "F4A32481242FCC67B57309D73F0641EA14C7B5E65B161A26EB2FB34640DF5220";
        RequireString(buggyReport, "crate-0.14.1", "BackendVersion");
        RequireString(fixedReport, "fix-6a131a9e", "BackendVersion");
        foreach (JsonElement report in new[] { buggyReport, fixedReport })
        {
            RequireString(report, fingerprint, "CandidateSetFingerprint");
            RequireInt32(report, 3, "DependencyRelevantCandidateCount");
            if (RequireArray(report, "Candidates").Length != 8 || RequireArray(report, "BudgetCurve").Length != 8)
            {
                throw new InvalidDataException("Expanded SlateDB artifact has an incomplete candidate or budget array.");
            }
        }

        RequireInt32(buggyReport, 3, "ViolationCandidateCount");
        RequireBoolean(buggyReport, true, "AllViolationsInsideDependencyClass");
        RequireInt32(fixedReport, 0, "ViolationCandidateCount");
        RequireBoolean(fixedReport, true, "AllViolationsInsideDependencyClass");
        findings.Add("slatedb-expanded=8-candidate-observer-grammar;buggy=3/3-dependency-violations;fixed=0");
        findings.Add("slatedb-expanded-fairness=dependency-class-prioritized;fingerprint-validated");
    }

    private static void ValidateExternalUnseededReplay(ZipArchive archive, List<string> findings)
    {
        using JsonDocument evidence = ReadJson(archive, "external-unseeded-replay.json");
        JsonElement report = RequireProperty(evidence.RootElement, "Report");
        RequireString(report, "external-unseeded-v1;five frozen external versions;uniform Fisher-Yates order", "GrammarIdentity");
        RequireInt32(report, 4, "TraceBudget");
        RequireInt32(report, 120_000, "TimeBudgetMilliseconds");
        RequireBoolean(report, true, "ExternalEvidence");
        RequireBoolean(report, true, "ReplayFromFrozenCandidateObservations");
        RequireBoolean(report, false, "LiveBackendReruns");
        if (RequireArray(report, "Seeds").Length != 32
            || RequireArray(report, "Candidates").Length != 46
            || RequireArray(report, "Runs").Length != 160)
        {
            throw new InvalidDataException("External unseeded replay does not contain the frozen 32-seed, five-family ledger.");
        }

        JsonElement counts = RequireProperty(report, "OutcomeCounts");
        RequireInt32(counts, 37, "NoFailure");
        RequireInt32(counts, 3, "KnownFailure");
        RequireInt32(counts, 94, "DuplicateRootCause");
        RequireInt32(counts, 0, "NewRootCauseCandidate");
        RequireInt32(counts, 26, "FalsePositive");
        RequireInt32(counts, 0, "OracleAmbiguity");
        RequireInt32(counts, 0, "HarnessEnvironmentFailure");
        findings.Add("external-unseeded=5-versions;32-seeds;160-replays;budget4");
        findings.Add("external-unseeded-outcomes=no-failure37;known3;duplicate94;false-positive26;new0;ambiguity0;harness0");
        findings.Add("external-unseeded-limit=deterministic-replay-over-frozen-observations;not-160-live-reruns");
    }

    private static void ValidateDoltRobustness(ZipArchive archive, List<string> findings)
    {
        int[] expectedDelays = [0, 1, 5, 10, 50, 100, 500];
        string[] expectedTargets =
        [
            "dolt-223",
            "dolt-230",
            "dolt-main-unpatched",
            "dolt-main-background",
            "dolt-main-without-cancel",
        ];
        JsonDocument[] summaries = archive.Entries
            .Where(entry => entry.FullName.EndsWith("summary.json", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(new StreamReader(entry.Open()).ReadToEnd()))
            .ToArray();
        try
        {
            if (summaries.Length != expectedDelays.Length * expectedTargets.Length)
            {
                throw new InvalidDataException($"Dolt robustness archive expected 35 summaries but found {summaries.Length}.");
            }

            HashSet<(string Target, int Delay)> cells = [];
            foreach (JsonDocument document in summaries)
            {
                JsonElement summary = document.RootElement;
                string target = RequireString(summary, "target");
                int delay = RequireInt32(summary, "delay_ms");
                if (!expectedTargets.Contains(target, StringComparer.Ordinal)
                    || !expectedDelays.Contains(delay)
                    || !cells.Add((target, delay)))
                {
                    throw new InvalidDataException($"Dolt robustness contains an unexpected or duplicate cell {target}/{delay}.");
                }

                RequireInt32(summary, 100, "runs");
                int reports = RequireInt32(summary, "report_runs");
                int harnessFailures = RequireInt32(summary, "harness_failures");
                if (reports + harnessFailures != 100)
                {
                    throw new InvalidDataException($"Dolt robustness cell {target}/{delay} loses outcomes.");
                }
                _ = RequireProperty(summary, "outcomes");
                _ = RequireProperty(summary, "relations");
                _ = RequireProperty(summary, "generated_ids");
                _ = RequireProperty(summary, "server_health");
                _ = RequireProperty(summary, "elapsed_ms");
            }

            findings.Add("dolt-robustness=35-cells;3500-repetitions;7-delays;5-targets");
            findings.Add("dolt-robustness-preservation=outcomes;relations;ids;server-health;timing;harness-failures");
        }
        finally
        {
            foreach (JsonDocument summary in summaries)
            {
                summary.Dispose();
            }
        }
    }

    private static string ResolveArchivePath(string baseDirectory, string relativeArchive)
    {
        string absoluteBase = Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar;
        string absoluteArchive = Path.GetFullPath(Path.Combine(baseDirectory, relativeArchive));
        if (!absoluteArchive.StartsWith(absoluteBase, StringComparison.Ordinal))
        {
            throw new InvalidDataException("External evidence archive escapes the manifest directory.");
        }

        return absoluteArchive;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static JsonDocument ReadJson(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"Archive entry '{entryName}' is missing.");
        if (entry.Length == 0)
        {
            throw new InvalidDataException($"Archive entry '{entryName}' is empty.");
        }

        using Stream stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string ReadText(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"Archive entry '{entryName}' is missing.");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void RequireRelationStatus(JsonElement root, string relationId, string expectedStatus)
    {
        JsonElement report = RequireProperty(root, "Report");
        JsonElement[] relations = RequireArray(report, "Relations");
        JsonElement relation = relations.FirstOrDefault(element =>
            string.Equals(RequireString(element, "RelationId"), relationId, StringComparison.Ordinal));
        if (relation.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Relation '{relationId}' is missing.");
        }

        RequireString(relation, expectedStatus, "Status");
    }

    private static void RequireBaselineStatus(JsonElement root, string baselineId, string expectedStatus)
    {
        JsonElement report = RequireProperty(root, "Report");
        JsonElement[] baselines = RequireArray(report, "Baselines");
        JsonElement baseline = baselines.FirstOrDefault(element =>
            string.Equals(RequireString(element, "BaselineId"), baselineId, StringComparison.Ordinal));
        if (baseline.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Baseline '{baselineId}' is missing.");
        }

        RequireString(baseline, expectedStatus, "Status");
    }

    private static void RequireRecipe(
        IReadOnlyList<JsonElement> recipes,
        string recipeName,
        bool sequenceRelevant,
        string continuationStatus,
        string branchGrammarStatus)
    {
        JsonElement recipe = recipes.FirstOrDefault(element =>
            string.Equals(RequireString(element, "Recipe"), recipeName, StringComparison.Ordinal));
        if (recipe.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Dolt recipe '{recipeName}' is missing.");
        }

        RequireBoolean(recipe, sequenceRelevant, "SequenceStateRelevant");
        RequireString(recipe, continuationStatus, "ContinuationRelation");
        RequireString(recipe, branchGrammarStatus, "BranchGrammarBaseline");
    }

    private static JsonElement RequireProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property)
            ? property
            : throw new InvalidDataException($"JSON property '{propertyName}' is missing.");

    private static JsonElement[] RequireArray(JsonElement element, string propertyName)
    {
        JsonElement array = RequireProperty(element, propertyName);
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"JSON property '{propertyName}' is not an array.");
        }

        JsonElement[] values = array.EnumerateArray().ToArray();
        if (values.Length == 0)
        {
            throw new InvalidDataException($"JSON array '{propertyName}' is empty.");
        }

        return values;
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        JsonElement property = RequireProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : throw new InvalidDataException($"JSON property '{propertyName}' is not a string.");
    }

    private static void RequireString(JsonElement element, string expected, params string[] propertyPath)
    {
        JsonElement property = Follow(element, propertyPath);
        string actual = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : throw new InvalidDataException($"JSON property '{string.Join('.', propertyPath)}' is not a string.");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"JSON property '{string.Join('.', propertyPath)}' expected '{expected}' but found '{actual}'.");
        }
    }

    private static void RequireStringPrefix(JsonElement element, string expectedPrefix, params string[] propertyPath)
    {
        JsonElement property = Follow(element, propertyPath);
        string actual = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : throw new InvalidDataException($"JSON property '{string.Join('.', propertyPath)}' is not a string.");
        if (!actual.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"JSON property '{string.Join('.', propertyPath)}' expected prefix '{expectedPrefix}' but found '{actual}'.");
        }
    }

    private static void RequireBoolean(JsonElement element, bool expected, params string[] propertyPath)
    {
        JsonElement property = Follow(element, propertyPath);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"JSON property '{string.Join('.', propertyPath)}' is not a boolean.");
        }
        bool actual = property.GetBoolean();
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"JSON property '{string.Join('.', propertyPath)}' expected {expected} but found {actual}.");
        }
    }

    private static void RequireInt32(JsonElement element, int expected, params string[] propertyPath)
    {
        JsonElement property = Follow(element, propertyPath);
        if (!property.TryGetInt32(out int actual))
        {
            throw new InvalidDataException($"JSON property '{string.Join('.', propertyPath)}' is not an Int32.");
        }
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"JSON property '{string.Join('.', propertyPath)}' expected {expected} but found {actual}.");
        }
    }

    private static int RequireInt32(JsonElement element, string propertyName)
    {
        JsonElement property = RequireProperty(element, propertyName);
        if (!property.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"JSON property '{propertyName}' is not an Int32.");
        }

        return value;
    }

    private static void RequireDouble(JsonElement element, double expected, params string[] propertyPath)
    {
        JsonElement property = Follow(element, propertyPath);
        if (!property.TryGetDouble(out double actual))
        {
            throw new InvalidDataException($"JSON property '{string.Join('.', propertyPath)}' is not numeric.");
        }
        if (Math.Abs(actual - expected) > 1e-12)
        {
            throw new InvalidDataException(
                $"JSON property '{string.Join('.', propertyPath)}' expected {expected} but found {actual}.");
        }
    }

    private static int RequireDictionaryCount(
        JsonElement element,
        string dictionaryProperty,
        string key,
        int expected)
    {
        JsonElement dictionary = RequireProperty(element, dictionaryProperty);
        JsonElement value = RequireProperty(dictionary, key);
        if (!value.TryGetInt32(out int actual))
        {
            throw new InvalidDataException($"JSON count '{dictionaryProperty}.{key}' is not an Int32.");
        }
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"JSON count '{dictionaryProperty}.{key}' expected {expected} but found {actual}.");
        }
        return actual;
    }

    private static int RequireDictionaryCountAtLeast(
        JsonElement element,
        string dictionaryProperty,
        string key,
        int minimum)
    {
        JsonElement dictionary = RequireProperty(element, dictionaryProperty);
        JsonElement value = RequireProperty(dictionary, key);
        if (!value.TryGetInt32(out int actual))
        {
            throw new InvalidDataException($"JSON count '{dictionaryProperty}.{key}' is not an Int32.");
        }
        if (actual < minimum)
        {
            throw new InvalidDataException(
                $"JSON count '{dictionaryProperty}.{key}' expected at least {minimum} but found {actual}.");
        }
        return actual;
    }

    private static int RequireContainingDictionaryCount(
        JsonElement element,
        string dictionaryProperty,
        string keyFragment)
    {
        JsonElement dictionary = RequireProperty(element, dictionaryProperty);
        foreach (JsonProperty property in dictionary.EnumerateObject())
        {
            if (!property.Name.Contains(keyFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!property.Value.TryGetInt32(out int count))
            {
                throw new InvalidDataException($"JSON count '{dictionaryProperty}.{property.Name}' is not an Int32.");
            }
            return count;
        }

        throw new InvalidDataException(
            $"JSON dictionary '{dictionaryProperty}' contains no key matching '{keyFragment}'.");
    }

    private static JsonElement Follow(JsonElement element, IReadOnlyList<string> propertyPath)
    {
        JsonElement current = element;
        foreach (string propertyName in propertyPath)
        {
            current = RequireProperty(current, propertyName);
        }
        return current;
    }
}
