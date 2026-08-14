namespace ChronicleDB.BranchCheck;

public enum SlateDbObserverCandidate
{
    ParentDbReader,
    CloneDb,
    CloneDbReader,
}

public sealed record SlateDbObserverCandidateEvidence(
    SlateDbObserverCandidate Candidate,
    int ReadableKeys,
    int TotalKeys,
    bool ViolatesExpectedReadability);

public sealed record SlateDbTriggerBudgetPoint(
    int CandidateBudget,
    int ExhaustiveOrderings,
    int GenericOrderingsDetected,
    double GenericDetectionRate,
    double RelationGuidedDetectionRate);

public sealed record SlateDbTriggerBudgetReport(
    string BackendVersion,
    SlateDbObserverCandidate GuidedCandidate,
    IReadOnlyList<SlateDbObserverCandidateEvidence> Candidates,
    IReadOnlyList<SlateDbTriggerBudgetPoint> BudgetCurve,
    int ViolationCandidateCount,
    bool GuidedCandidateIsViolation);

public static class SlateDbTriggerBudgetCampaign
{
    public static SlateDbTriggerBudgetReport Evaluate(SlateDbObserverObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var evidence = new[]
        {
            Evidence(
                SlateDbObserverCandidate.ParentDbReader,
                observation.ParentReaderReadableKeys,
                observation.TotalKeys),
            Evidence(
                SlateDbObserverCandidate.CloneDb,
                observation.DbReadableKeys,
                observation.TotalKeys),
            Evidence(
                SlateDbObserverCandidate.CloneDbReader,
                observation.DbReaderReadableKeys,
                observation.TotalKeys),
        };

        SlateDbObserverCandidate guided = SlateDbObserverCandidate.CloneDbReader;
        bool guidedViolation = evidence.Single(item => item.Candidate == guided).ViolatesExpectedReadability;
        IReadOnlyList<SlateDbObserverCandidate[]> orderings = GeneratePermutations(
            Enum.GetValues<SlateDbObserverCandidate>());
        var curve = new List<SlateDbTriggerBudgetPoint>(evidence.Length);
        for (int budget = 1; budget <= evidence.Length; budget++)
        {
            int detected = orderings.Count(ordering =>
                ordering.Take(budget).Any(candidate =>
                    evidence.Single(item => item.Candidate == candidate).ViolatesExpectedReadability));
            curve.Add(new SlateDbTriggerBudgetPoint(
                budget,
                orderings.Count,
                detected,
                detected / (double)orderings.Count,
                guidedViolation ? 1.0 : 0.0));
        }

        return new SlateDbTriggerBudgetReport(
            observation.Version,
            guided,
            evidence,
            curve,
            evidence.Count(static item => item.ViolatesExpectedReadability),
            guidedViolation);
    }

    public static IReadOnlyList<SlateDbObserverCandidate[]> GeneratePermutations(
        IReadOnlyList<SlateDbObserverCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var working = candidates.ToArray();
        var output = new List<SlateDbObserverCandidate[]>();
        Permute(working, 0, output);
        return output;
    }

    private static SlateDbObserverCandidateEvidence Evidence(
        SlateDbObserverCandidate candidate,
        int readable,
        int total)
        => new(candidate, readable, total, readable != total);

    private static void Permute(
        SlateDbObserverCandidate[] working,
        int index,
        ICollection<SlateDbObserverCandidate[]> output)
    {
        if (index == working.Length)
        {
            output.Add((SlateDbObserverCandidate[])working.Clone());
            return;
        }

        for (int current = index; current < working.Length; current++)
        {
            (working[index], working[current]) = (working[current], working[index]);
            Permute(working, index + 1, output);
            (working[index], working[current]) = (working[current], working[index]);
        }
    }
}
