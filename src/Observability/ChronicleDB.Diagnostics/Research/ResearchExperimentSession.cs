namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Binds immutable manifest and workload inputs to one eventual trace artifact.
/// Candidate runners may use this orchestration shell without granting it engine
/// authority or allowing an experiment to rewrite its inputs.
/// </summary>
public sealed class ResearchExperimentSession
{
    private readonly ResearchArtifactWriter _artifactWriter;
    private bool _traceCompleted;

    public ResearchExperimentSession(
        ResearchArtifactWriter artifactWriter,
        ExperimentManifest manifest,
        IEnumerable<ResearchWorkloadOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(artifactWriter);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(operations);

        _artifactWriter = artifactWriter;
        Manifest = manifest;
        Workload = operations.ToArray();
        ManifestArtifact = _artifactWriter.WriteManifest(manifest);
        WorkloadArtifact = _artifactWriter.WriteWorkload(Workload);
    }

    public ExperimentManifest Manifest { get; }

    public IReadOnlyList<ResearchWorkloadOperation> Workload { get; }

    public ResearchManifestArtifact ManifestArtifact { get; }

    public ResearchWorkloadArtifact WorkloadArtifact { get; }

    public bool TraceCompleted => _traceCompleted;

    public ResearchTraceArtifact Complete(IEnumerable<ResearchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_traceCompleted)
        {
            throw new InvalidOperationException("A research experiment trace can only be completed once.");
        }

        var artifact = _artifactWriter.WriteTrace(events);
        _traceCompleted = true;
        return artifact;
    }
}
