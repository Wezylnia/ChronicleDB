using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchCampaignRegistrationTests
{
    [Fact]
    public void CanonicalRegistrationIsStableAcrossRunOrdering()
    {
        var first = CreateRegistration([
            Run(2, ResearchCampaignPartition.HoldoutA, 101),
            Run(1, ResearchCampaignPartition.PilotA, 1),
            Run(3, ResearchCampaignPartition.HoldoutB, 201),
        ]);
        var second = CreateRegistration([
            Run(3, ResearchCampaignPartition.HoldoutB, 201),
            Run(1, ResearchCampaignPartition.PilotA, 1),
            Run(2, ResearchCampaignPartition.HoldoutA, 101),
        ]);

        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(first.ComputeCanonicalSha256(), second.ComputeCanonicalSha256());
    }

    [Fact]
    public void HoldoutPartitionsMustBeSealedTogether()
    {
        var registration = CreateRegistration([
            Run(1, ResearchCampaignPartition.PilotA, 1),
            Run(2, ResearchCampaignPartition.HoldoutA, 101),
        ]);

        Assert.Throws<InvalidOperationException>(registration.Validate);
    }

    [Fact]
    public void HoldoutPartitionsCannotReuseInputIdentity()
    {
        var registration = CreateRegistration([
            Run(1, ResearchCampaignPartition.HoldoutA, 101),
            Run(2, ResearchCampaignPartition.HoldoutB, 101),
        ]);

        Assert.Throws<InvalidOperationException>(registration.Validate);
    }

    [Fact]
    public void ArtifactWriterRejectsLaterRegistrationRewrite()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        var first = CreateRegistration([
            Run(1, ResearchCampaignPartition.PilotA, 1),
            Run(2, ResearchCampaignPartition.HoldoutA, 101),
            Run(3, ResearchCampaignPartition.HoldoutB, 201),
        ]);
        var artifact = writer.WriteCampaignRegistration(first);

        Assert.Equal(first.ComputeCanonicalSha256(), artifact.Sha256);
        var changed = first with { CandidateConfigHash = "changed-config" };
        Assert.Throws<IOException>(() => writer.WriteCampaignRegistration(changed));
    }

    [Fact]
    public void CandidateGateDecisionRequiresEvidenceAndIsImmutable()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ResearchArtifactWriter(directory.Path);
        var decision = new ResearchCandidateGateDecision
        {
            FormatVersion = ResearchCandidateGateDecision.CurrentFormatVersion,
            CandidateId = "A1",
            Disposition = ResearchCandidateDisposition.Supported,
            NarrowClaimVersion = "a1-observer-exact-v2",
            Rationale = "Exact logical retention separates from the coarse horizon in preregistered workloads.",
            UtcRecordedAt = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero),
            EvidenceSha256 = [Hash('a'), Hash('b')],
        };

        var artifact = writer.WriteCandidateGateDecision(decision);
        Assert.Equal(decision.ComputeCanonicalSha256(), artifact.Sha256);
        Assert.Throws<IOException>(() => writer.WriteCandidateGateDecision(decision with
        {
            Disposition = ResearchCandidateDisposition.Weakened,
        }));
    }

    [Fact]
    public void CandidateGateDecisionRejectsMissingEvidence()
    {
        var decision = new ResearchCandidateGateDecision
        {
            FormatVersion = ResearchCandidateGateDecision.CurrentFormatVersion,
            CandidateId = "A9",
            Disposition = ResearchCandidateDisposition.Weakened,
            NarrowClaimVersion = "a9-resource-authority-v1",
            Rationale = "History-specific separation is not observed against the resource-only baseline.",
            UtcRecordedAt = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero),
            EvidenceSha256 = [],
        };

        Assert.Throws<InvalidOperationException>(decision.Validate);
    }

    private static ResearchCampaignRegistration CreateRegistration(IReadOnlyList<ResearchCampaignRunRegistration> runs)
        => new()
        {
            FormatVersion = ResearchCampaignRegistration.CurrentFormatVersion,
            CandidateId = "A1",
            CandidateConfigHash = "candidate-config-v1",
            NoveltyCardVersion = "a1-v2",
            FailureModelVersion = "persistence-v1",
            UtcSealedAt = new DateTimeOffset(2026, 8, 12, 7, 0, 0, TimeSpan.Zero),
            Runs = runs,
        };

    private static ResearchCampaignRunRegistration Run(int trialOrder, ResearchCampaignPartition partition, int seed)
        => new(
            Guid.Parse($"00000000-0000-0000-0000-{trialOrder:D12}"),
            partition,
            seed,
            seed + 1,
            seed + 2,
            1,
            "machine-a",
            trialOrder,
            Hash((char)('a' + trialOrder)));

    private static string Hash(char value) => new(value, 64);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronicle-campaign-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
