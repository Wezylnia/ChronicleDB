using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ShadowRetentionHoldoutPlanTests
{
    private static readonly double[] ExpectedQuantiles = [0.05d, 0.50d, 0.95d];
    private static readonly string[] ExpectedNegativeControls = ["holdout-neg-b08-s001"];

    [Fact]
    public void DefaultPlanSealsBothHoldoutPartitionsBeforeExecution()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var first = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var second = ShadowRetentionHoldoutExecutionPlan.Create(publication);

        Assert.Equal(210, first.HoldoutARunCount);
        Assert.Equal(210, first.HoldoutBRunCount);
        Assert.Equal(420, first.Runs.Count);
        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
        first.ValidateAgainst(publication);

        foreach (var partition in Enum.GetValues<ShadowRetentionHoldoutPartition>())
        {
            var runs = first.Runs.Where(run => run.Partition == partition).ToArray();
            Assert.Equal(Enumerable.Range(0, 210), runs.Select(run => run.TrialOrder));
            foreach (var group in runs.GroupBy(run => run.CaseId, StringComparer.Ordinal))
            {
                Assert.Equal(30, group.Count());
            }
        }

        var aSeeds = first.Runs.Where(run => run.Partition == ShadowRetentionHoldoutPartition.HoldoutA)
            .Select(run => run.Seed).Distinct().Order().ToArray();
        var bSeeds = first.Runs.Where(run => run.Partition == ShadowRetentionHoldoutPartition.HoldoutB)
            .Select(run => run.Seed).Distinct().Order().ToArray();
        Assert.Equal(publication.HoldoutASeeds, aSeeds);
        Assert.Equal(publication.HoldoutBSeeds, bSeeds);
        Assert.Empty(aSeeds.Intersect(bSeeds));
    }

    [Fact]
    public void HoldoutAnalysisPlanFreezesNegativeControlAndQuantiles()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var execution = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publication, execution);

        Assert.Equal(ShadowRetentionHoldoutPartition.HoldoutA, analysis.InitialPartition);
        Assert.Equal(7, analysis.CasesPerPartition);
        Assert.Equal(30, analysis.RunsPerCase);
        Assert.Equal(ExpectedQuantiles, analysis.Quantiles);
        Assert.Equal("linear-interpolation-index=(n-1)*p", analysis.QuantileMethod);
        Assert.Equal(ExpectedNegativeControls, analysis.NegativeControlCaseIds);
        Assert.Contains(analysis.ReportingRules, rule => rule.Contains("Exclude no successful", StringComparison.Ordinal));
        Assert.Contains(analysis.ReportingRules, rule => rule.Contains("Do not read or execute Holdout-B", StringComparison.Ordinal));
        analysis.ValidateAgainst(publication, execution);
    }

    [Fact]
    public void HoldoutArtifactsAreImmutable()
    {
        using var directory = new TemporaryDirectory();
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var execution = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publication, execution);

        var firstExecution = ShadowRetentionHoldoutExecutionPlanWriter.Write(directory.Path, execution);
        var repeatedExecution = ShadowRetentionHoldoutExecutionPlanWriter.Write(directory.Path, execution);
        Assert.Equal(firstExecution.Sha256, repeatedExecution.Sha256);

        var firstAnalysis = ShadowRetentionHoldoutAnalysisPlanWriter.Write(directory.Path, analysis);
        var repeatedAnalysis = ShadowRetentionHoldoutAnalysisPlanWriter.Write(directory.Path, analysis);
        Assert.Equal(firstAnalysis.Sha256, repeatedAnalysis.Sha256);

        Assert.Throws<IOException>(() => ShadowRetentionHoldoutAnalysisPlanWriter.Write(
            directory.Path,
            analysis with { CandidateId = "changed" }));
    }

    [Fact]
    public void HoldoutExecutionIdentityChangesWhenSeedPartitionChanges()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var changed = publication with
        {
            HoldoutASeeds = publication.HoldoutASeeds.Select(seed => checked(seed + 100)).ToArray(),
        };

        var first = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var second = ShadowRetentionHoldoutExecutionPlan.Create(changed);

        Assert.NotEqual(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
    }


    [Fact]
    public void ExecutionPlanRejectsPostFreezeCaseMutation()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var plan = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var runs = plan.Runs.ToArray();
        runs[0] = runs[0] with { ShadowFraction = Math.Min(1d, runs[0].ShadowFraction + 0.01d) };
        var mutated = plan with { Runs = runs };

        Assert.Throws<InvalidOperationException>(() => mutated.ValidateAgainst(publication));
    }

    [Fact]
    public void AnalysisPlanRejectsPostFreezeMetricOrRuleMutation()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var execution = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publication, execution);

        Assert.Throws<InvalidOperationException>(() => (analysis with
        {
            PrimaryMetrics = ["measured-reclamation-ratio"],
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (analysis with
        {
            ReportingRules = ["Report only favorable cases."],
        }).Validate());
    }


    [Fact]
    public void HoldoutRegistrationBindsPlansSourceEnvironmentAndBinaries()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var execution = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publication, execution);
        var registration = CreateRegistration(publication, execution, analysis);

        registration.ValidateAgainst(publication, execution, analysis);
        Assert.Equal(210, registration.HoldoutARunCount);
        Assert.Equal(210, registration.HoldoutBRunCount);
        Assert.Equal(ShadowRetentionHoldoutPartition.HoldoutA, registration.InitialPartition);
        Assert.True(registration.HoldoutBSealedBeforeA);
        Assert.Equal(["ChronicleDB.A1ShadowRetentionPilot.dll", "ChronicleDB.Diagnostics.dll"],
            registration.BinaryArtifacts.Select(artifact => artifact.Name));
    }

    [Fact]
    public void HoldoutRegistrationRejectsSourceOrBinaryTampering()
    {
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var execution = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publication, execution);
        var registration = CreateRegistration(publication, execution, analysis);

        Assert.Throws<InvalidOperationException>(() => (registration with
        {
            SourceTreeClean = false,
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (registration with
        {
            ExpectedMainBaseIsAncestor = false,
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (registration with
        {
            BinaryArtifacts = registration.BinaryArtifacts.Reverse().ToArray(),
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (registration with
        {
            HoldoutAnalysisPlanSha256 = new string('f', 64),
        }).ValidateAgainst(publication, execution, analysis));
    }

    [Fact]
    public void HoldoutRegistrationArtifactIsImmutable()
    {
        using var directory = new TemporaryDirectory();
        var publication = ShadowRetentionPublicationPlan.CreateDefault();
        var execution = ShadowRetentionHoldoutExecutionPlan.Create(publication);
        var analysis = ShadowRetentionHoldoutAnalysisPlan.Create(publication, execution);
        var registration = CreateRegistration(publication, execution, analysis);

        var first = ShadowRetentionHoldoutRegistrationWriter.Write(directory.Path, registration);
        var repeated = ShadowRetentionHoldoutRegistrationWriter.Write(directory.Path, registration);
        Assert.Equal(first.Sha256, repeated.Sha256);
        Assert.Throws<IOException>(() => ShadowRetentionHoldoutRegistrationWriter.Write(
            directory.Path,
            registration with { MachineBlockId = "different-machine-block" }));
    }

    private static ShadowRetentionHoldoutRegistration CreateRegistration(
        ShadowRetentionPublicationPlan publication,
        ShadowRetentionHoldoutExecutionPlan execution,
        ShadowRetentionHoldoutAnalysisPlan analysis)
        => new()
        {
            FormatVersion = ShadowRetentionHoldoutRegistration.CurrentFormatVersion,
            CandidateId = publication.CandidateId,
            PublicationPlanSha256 = publication.ComputeCanonicalSha256(),
            HoldoutExecutionPlanSha256 = execution.ComputeCanonicalSha256(),
            HoldoutAnalysisPlanSha256 = analysis.ComputeCanonicalSha256(),
            ExpectedMainBaseCommit = new string('a', 40),
            SourceCommit = new string('b', 40),
            SourceTree = new string('c', 40),
            SourceTreeClean = true,
            ExpectedMainBaseIsAncestor = true,
            MachineBlockId = "machine-block-test",
            FrameworkDescription = ".NET test",
            OsDescription = "test-os",
            ProcessArchitecture = "X64",
            OsArchitecture = "X64",
            HoldoutARunCount = execution.HoldoutARunCount,
            HoldoutBRunCount = execution.HoldoutBRunCount,
            InitialPartition = ShadowRetentionHoldoutPartition.HoldoutA,
            HoldoutBSealedBeforeA = true,
            BinaryArtifacts =
            [
                new ShadowRetentionBinaryArtifactIdentity
                {
                    Name = "ChronicleDB.A1ShadowRetentionPilot.dll",
                    LengthBytes = 123,
                    Sha256 = new string('d', 64),
                },
                new ShadowRetentionBinaryArtifactIdentity
                {
                    Name = "ChronicleDB.Diagnostics.dll",
                    LengthBytes = 456,
                    Sha256 = new string('e', 64),
                },
            ],
        };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "chronicle-a1-holdout-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
