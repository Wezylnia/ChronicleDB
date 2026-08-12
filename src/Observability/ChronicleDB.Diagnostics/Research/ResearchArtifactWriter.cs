using System.Security.Cryptography;
using System.Text;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Writes immutable, content-addressed research artifacts beside an experiment run.
/// This class is deliberately outside engine authority: a failed artifact write must
/// never change ChronicleDB semantics or durability decisions.
/// </summary>
public sealed class ResearchArtifactWriter
{
    public const string ManifestFileName = "manifest.json";
    public const string ManifestHashFileName = "manifest.sha256";
    public const string TraceFileName = "trace.json";
    public const string TraceHashFileName = "trace.sha256";
    public const string WorkloadFileName = "workload.json";
    public const string WorkloadHashFileName = "workload.sha256";
    public const string CrashPlanFileName = "crash-plan.json";
    public const string CrashPlanHashFileName = "crash-plan.sha256";
    public const string CampaignRegistrationFileName = "campaign-registration.json";
    public const string CampaignRegistrationHashFileName = "campaign-registration.sha256";
    public const string CandidateGateDecisionFileName = "candidate-gate.json";
    public const string CandidateGateDecisionHashFileName = "candidate-gate.sha256";
    public const string ResearchGateReportFileName = "research-gate-report.json";
    public const string ResearchGateReportHashFileName = "research-gate-report.sha256";

    public ResearchArtifactWriter(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public ResearchManifestArtifact WriteManifest(ExperimentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var canonicalJson = manifest.SerializeCanonical();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();

        WriteImmutable(ManifestFileName, canonicalJson);
        WriteImmutable(ManifestHashFileName, hash);

        return new ResearchManifestArtifact(
            Path.Combine(DirectoryPath, ManifestFileName),
            Path.Combine(DirectoryPath, ManifestHashFileName),
            hash);
    }

    public ResearchTraceArtifact WriteTrace(IEnumerable<ResearchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var canonicalJson = ResearchTraceSerializer.SerializeCanonical(events);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();

        WriteImmutable(TraceFileName, canonicalJson);
        WriteImmutable(TraceHashFileName, hash);

        return new ResearchTraceArtifact(
            Path.Combine(DirectoryPath, TraceFileName),
            Path.Combine(DirectoryPath, TraceHashFileName),
            hash);
    }

    public ResearchWorkloadArtifact WriteWorkload(IEnumerable<ResearchWorkloadOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var canonicalJson = ResearchWorkloadSerializer.SerializeCanonical(operations);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();

        WriteImmutable(WorkloadFileName, canonicalJson);
        WriteImmutable(WorkloadHashFileName, hash);

        return new ResearchWorkloadArtifact(
            Path.Combine(DirectoryPath, WorkloadFileName),
            Path.Combine(DirectoryPath, WorkloadHashFileName),
            hash);
    }

    public ResearchCampaignRegistrationArtifact WriteCampaignRegistration(ResearchCampaignRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var canonicalJson = registration.SerializeCanonical();
        var hash = registration.ComputeCanonicalSha256();
        WriteImmutable(CampaignRegistrationFileName, canonicalJson);
        WriteImmutable(CampaignRegistrationHashFileName, hash);

        return new ResearchCampaignRegistrationArtifact(
            Path.Combine(DirectoryPath, CampaignRegistrationFileName),
            Path.Combine(DirectoryPath, CampaignRegistrationHashFileName),
            hash);
    }

    public ResearchCandidateGateDecisionArtifact WriteCandidateGateDecision(ResearchCandidateGateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var canonicalJson = decision.SerializeCanonical();
        var hash = decision.ComputeCanonicalSha256();
        WriteImmutable(CandidateGateDecisionFileName, canonicalJson);
        WriteImmutable(CandidateGateDecisionHashFileName, hash);

        return new ResearchCandidateGateDecisionArtifact(
            Path.Combine(DirectoryPath, CandidateGateDecisionFileName),
            Path.Combine(DirectoryPath, CandidateGateDecisionHashFileName),
            hash);
    }

    public ResearchGateReportArtifact WriteResearchGateReport(ResearchGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var canonicalJson = report.SerializeCanonical();
        var hash = report.ComputeCanonicalSha256();
        WriteImmutable(ResearchGateReportFileName, canonicalJson);
        WriteImmutable(ResearchGateReportHashFileName, hash);

        return new ResearchGateReportArtifact(
            Path.Combine(DirectoryPath, ResearchGateReportFileName),
            Path.Combine(DirectoryPath, ResearchGateReportHashFileName),
            hash);
    }

    public ResearchCrashPlanArtifact WriteCrashPlan(ResearchCrashPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var canonicalJson = ResearchCrashPlanSerializer.SerializeCanonical(plan);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();

        WriteImmutable(CrashPlanFileName, canonicalJson);
        WriteImmutable(CrashPlanHashFileName, hash);

        return new ResearchCrashPlanArtifact(
            Path.Combine(DirectoryPath, CrashPlanFileName),
            Path.Combine(DirectoryPath, CrashPlanHashFileName),
            hash);
    }

    private void WriteImmutable(string fileName, string content)
    {
        var destination = Path.Combine(DirectoryPath, fileName);

        if (File.Exists(destination))
        {
            EnsureSameContent(destination, content);
            return;
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                EnsureSameContent(destination, content);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureSameContent(string destination, string expected)
    {
        var actual = File.ReadAllText(destination, Encoding.UTF8);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new IOException($"Research artifact already exists with different content: {destination}");
        }
    }
}

public sealed record ResearchManifestArtifact(
    string ManifestPath,
    string ManifestHashPath,
    string Sha256);

public sealed record ResearchTraceArtifact(
    string TracePath,
    string TraceHashPath,
    string Sha256);

public sealed record ResearchWorkloadArtifact(
    string WorkloadPath,
    string WorkloadHashPath,
    string Sha256);

public sealed record ResearchCrashPlanArtifact(
    string CrashPlanPath,
    string CrashPlanHashPath,
    string Sha256);

public sealed record ResearchCampaignRegistrationArtifact(
    string RegistrationPath,
    string RegistrationHashPath,
    string Sha256);

public sealed record ResearchCandidateGateDecisionArtifact(
    string DecisionPath,
    string DecisionHashPath,
    string Sha256);

public sealed record ResearchGateReportArtifact(
    string ReportPath,
    string ReportHashPath,
    string Sha256);
