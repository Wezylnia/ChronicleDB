namespace ChronicleDB.BranchCheck;

public sealed record BranchCheckFailureSignature(IReadOnlyList<string> RelationKeys)
{
    public static BranchCheckFailureSignature Capture(BranchScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
        string[] keys = report.Relations
            .Where(static relation => relation.Status == RelationStatus.Fail)
            .Select(static relation => relation.RelationId + ":" + relation.Family)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        return new BranchCheckFailureSignature(keys);
    }

    public bool IsFailure => RelationKeys.Count > 0;

    public bool Matches(BranchScenario scenario)
        => RelationKeys.SequenceEqual(
            Capture(scenario).RelationKeys,
            StringComparer.Ordinal);
}

public sealed record TraceReductionReport(
    int OriginalFrameCount,
    int ReducedFrameCount,
    int RemovalAttempts)
{
    public int RemovedFrameCount => OriginalFrameCount - ReducedFrameCount;
}

public sealed record TraceReductionResult(
    BranchScenario Scenario,
    TraceReductionReport Report,
    BranchCheckFailureSignature Signature);

/// <summary>
/// Greedily removes trace frames while preserving a semantic BranchCheck failure.
/// The reducer never treats a process crash or issue identifier as a signature.
/// </summary>
public static class BranchScenarioReducer
{
    public static TraceReductionResult Reduce(
        BranchScenario scenario,
        Func<BranchScenario, bool> preservesFailure)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(preservesFailure);

        BranchCheckFailureSignature originalSignature = BranchCheckFailureSignature.Capture(scenario);
        if (!originalSignature.IsFailure || !preservesFailure(scenario))
        {
            throw new ArgumentException("The original scenario does not satisfy the failure predicate.", nameof(scenario));
        }

        List<TraceFrame> frames = scenario.Frames.ToList();
        int originalFrameCount = frames.Count;
        int removalAttempts = 0;

        bool removed;
        do
        {
            removed = false;
            for (int index = 0; index < frames.Count; index++)
            {
                TraceFrame candidateFrame = frames[index];
                frames.RemoveAt(index);
                removalAttempts++;

                BranchScenario candidate = scenario with { Frames = frames.ToArray() };
                if (preservesFailure(candidate))
                {
                    removed = true;
                    index--;
                    continue;
                }

                frames.Insert(index, candidateFrame);
            }
        }
        while (removed);

        BranchScenario reduced = scenario with { Frames = frames.ToArray() };
        BranchCheckFailureSignature signature = BranchCheckFailureSignature.Capture(reduced);
        if (!signature.IsFailure || !originalSignature.Matches(reduced))
        {
            throw new InvalidOperationException("The reducer produced a scenario without a semantic failure signature.");
        }

        return new TraceReductionResult(
            reduced,
            new TraceReductionReport(originalFrameCount, frames.Count, removalAttempts),
            signature);
    }
}
