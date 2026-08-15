using System.Text.Json;
using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class ExternalEvidenceBundleTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    [Fact]
    public void FrozenExternalEvidenceBundleValidatesAllImportedArtifacts()
    {
        string repositoryRoot = FindRepositoryRoot();
        string manifestPath = Path.Combine(repositoryRoot, "artifacts", "external-frozen", "manifest.json");

        ExternalEvidenceBundleReport report = ExternalEvidenceBundleValidator.Validate(manifestPath);

        Assert.True(report.Passed);
        Assert.Equal(8, report.Artifacts.Count);
        Assert.Equal(3, report.ExternalSystemCount);
        Assert.All(report.Artifacts, artifact =>
        {
            Assert.True(artifact.DigestValid);
            Assert.True(artifact.StructureValid);
            Assert.True(artifact.SemanticValid);
            Assert.Empty(artifact.Errors);
        });
        Assert.Contains(
            report.Artifacts.Single(artifact => artifact.Key == "matrixone-live").Findings,
            finding => finding.Contains("B0/B2/B4-pass", StringComparison.Ordinal));
        Assert.Contains(
            report.Artifacts.Single(artifact => artifact.Key == "dolt-main-causal").Findings,
            finding => finding.Contains("20/20-pass", StringComparison.Ordinal));
        Assert.Contains(
            report.Artifacts.Single(artifact => artifact.Key == "dolt-expanded-fair-budget").Findings,
            finding => finding.Contains("2.2.3=6/10-violations", StringComparison.Ordinal));
        Assert.Contains(
            report.Artifacts.Single(artifact => artifact.Key == "slatedb-expanded-fair-observer").Findings,
            finding => finding.Contains("buggy=3/3-dependency-violations", StringComparison.Ordinal));
        Assert.Contains(
            report.Artifacts.Single(artifact => artifact.Key == "matrixone-v2-fair-identity").Findings,
            finding => finding.Contains("matrixone-v2-pinned-image", StringComparison.Ordinal));
    }


    [Fact]
    public void HistoricalUpstreamStatusSnapshotPreservesCloseReasonAndFixProvenance()
    {
        string repositoryRoot = FindRepositoryRoot();
        string statusPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "external-frozen",
            "historical-upstream-status-20260815.json");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statusPath));
        JsonElement cases = document.RootElement.GetProperty("cases");

        Assert.Equal(7, cases.GetArrayLength());

        JsonElement matrixOneOpen = cases.EnumerateArray().Single(entry =>
            entry.GetProperty("repository").GetString() == "matrixorigin/matrixone"
            && entry.GetProperty("issue").GetInt32() == 27092);
        Assert.Equal("open", matrixOneOpen.GetProperty("state").GetString());

        JsonElement matrixOneFixed = cases.EnumerateArray().Single(entry =>
            entry.GetProperty("repository").GetString() == "matrixorigin/matrixone"
            && entry.GetProperty("issue").GetInt32() == 26120);
        Assert.Equal(26310, matrixOneFixed.GetProperty("fixPr").GetInt32());
        Assert.Equal(
            "ccfcea46981aba349b4fa11445202939f1045c53",
            matrixOneFixed.GetProperty("fixCommit").GetString());

        JsonElement yugabyteNotPlanned = cases.EnumerateArray().Single(entry =>
            entry.GetProperty("repository").GetString() == "yugabyte/yugabyte-db"
            && entry.GetProperty("issue").GetInt32() == 32057);
        Assert.Equal("not_planned", yugabyteNotPlanned.GetProperty("stateReason").GetString());
        Assert.Equal("not-proven-fixed", yugabyteNotPlanned.GetProperty("fixStatus").GetString());

        JsonElement slateDbFixed = cases.EnumerateArray().Single(entry =>
            entry.GetProperty("repository").GetString() == "slatedb/slatedb"
            && entry.GetProperty("issue").GetInt32() == 1902);
        Assert.Equal(1907, slateDbFixed.GetProperty("fixPr").GetInt32());
        Assert.Equal(
            "6a131a9ebfd121ca553cb80a08b7b8f2bd142092",
            slateDbFixed.GetProperty("fixCommit").GetString());
    }

    [Fact]
    public void DigestMismatchFailsClosedBeforeSemanticValidation()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceArchive = Path.Combine(
            repositoryRoot,
            "artifacts",
            "external-frozen",
            "raw",
            "matrixone-final.zip");
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "branchcheck-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string archivePath = Path.Combine(temporaryDirectory, "matrixone.zip");
            File.Copy(sourceArchive, archivePath);
            var manifest = new ExternalEvidenceManifest(
                1,
                "Wezylnia/ChronicleDB",
                "research-head",
                "main-head",
                [
                    new ExternalEvidenceArtifact(
                        "matrixone-live",
                        "MatrixOneLive",
                        1,
                        1,
                        "head",
                        new string('0', 64),
                        "matrixone.zip",
                        "test",
                        [
                            "matrixone-continuation.json",
                            "matrixone-identity.json",
                            "matrixone-trigger-budget.json",
                            "matrixone.log",
                        ],
                        "test identity"),
                ]);
            string manifestPath = Path.Combine(temporaryDirectory, "manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, ManifestJsonOptions));

            ExternalEvidenceBundleReport report = ExternalEvidenceBundleValidator.Validate(manifestPath);

            ExternalEvidenceArtifactResult artifact = Assert.Single(report.Artifacts);
            Assert.False(report.Passed);
            Assert.False(artifact.DigestValid);
            Assert.False(artifact.StructureValid);
            Assert.False(artifact.SemanticValid);
            Assert.Contains(artifact.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChronicleDB.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ChronicleDB.slnx from the test output directory.");
    }
}
