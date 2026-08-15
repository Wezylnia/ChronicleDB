namespace ChronicleDB.BranchCheck;

public enum SlateDbObserverCandidate
{
    ParentDbReader,
    CloneDb,
    CloneDbReader,
}

public enum SlateDbExpandedObserverCandidate
{
    ParentDbReader,
    ParentDb,
    ParentDbReaderReopen,
    CloneDb,
    CloneDbReopen,
    CloneDbReader,
    CloneDbReaderReopen,
    CloneDbReaderAfterParentReopen,
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

public sealed record SlateDbExpandedCandidateEvidence(
    SlateDbExpandedObserverCandidate Candidate,
    int ReadableKeys,
    int TotalKeys,
    bool DependencyRelevant,
    bool ViolatesExpectedReadability,
    string? Error);

public sealed record SlateDbExpandedBudgetPoint(
    int CandidateBudget,
    long GenericOrderings,
    long GenericOrderingsDetected,
    double GenericDetectionRate,
    long GuidedOrderings,
    long GuidedOrderingsDetected,
    double GuidedDetectionRate);

public sealed record SlateDbExpandedTriggerBudgetReport(
    string BackendVersion,
    IReadOnlyList<SlateDbExpandedCandidateEvidence> Candidates,
    IReadOnlyList<SlateDbExpandedBudgetPoint> BudgetCurve,
    int ViolationCandidateCount,
    int DependencyRelevantCandidateCount,
    bool AllViolationsInsideDependencyClass,
    bool GuidedHasStrictAdvantageAtAnyBudget,
    string CandidateSetFingerprint,
    string FairnessNote);

public static class SlateDbExpandedTriggerBudgetCampaign
{
    public static IReadOnlyList<(SlateDbExpandedObserverCandidate Candidate, string Field)> CandidateFields { get; } =
    [
        (SlateDbExpandedObserverCandidate.ParentDbReader, "parent_db_reader"),
        (SlateDbExpandedObserverCandidate.ParentDb, "parent_db"),
        (SlateDbExpandedObserverCandidate.ParentDbReaderReopen, "parent_db_reader_reopen"),
        (SlateDbExpandedObserverCandidate.CloneDb, "clone_db"),
        (SlateDbExpandedObserverCandidate.CloneDbReopen, "clone_db_reopen"),
        (SlateDbExpandedObserverCandidate.CloneDbReader, "clone_db_reader"),
        (SlateDbExpandedObserverCandidate.CloneDbReaderReopen, "clone_db_reader_reopen"),
        (SlateDbExpandedObserverCandidate.CloneDbReaderAfterParentReopen, "clone_db_reader_after_parent_reopen"),
    ];

    public static string CandidateSetFingerprint { get; } = Fingerprint(CandidateFields);

    public static bool IsDependencyRelevant(SlateDbExpandedObserverCandidate candidate)
        => candidate is SlateDbExpandedObserverCandidate.CloneDbReader
            or SlateDbExpandedObserverCandidate.CloneDbReaderReopen
            or SlateDbExpandedObserverCandidate.CloneDbReaderAfterParentReopen;

    public static SlateDbExpandedTriggerBudgetReport Evaluate(SlateDbObserverObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ExpandedCandidates is null
            || observation.ExpandedCandidates.Count != CandidateFields.Count)
        {
            throw new ExternalAdapterException("SlateDB expanded budget requires all eight preregistered observer candidates.");
        }

        SlateDbExpandedCandidateEvidence[] evidence = observation.ExpandedCandidates
            .Select(item => new SlateDbExpandedCandidateEvidence(
                item.Candidate,
                item.ReadableKeys,
                item.TotalKeys,
                IsDependencyRelevant(item.Candidate),
                item.ReadableKeys != item.TotalKeys,
                item.Error))
            .ToArray();
        if (evidence.Select(static item => item.Candidate).Distinct().Count() != CandidateFields.Count)
        {
            throw new ExternalAdapterException("SlateDB expanded budget contains duplicate candidates.");
        }

        int candidateCount = evidence.Length;
        int relevantCount = evidence.Count(static item => item.DependencyRelevant);
        int violationCount = evidence.Count(static item => item.ViolatesExpectedReadability);
        int relevantViolations = evidence.Count(static item => item.DependencyRelevant && item.ViolatesExpectedReadability);
        int controlCount = candidateCount - relevantCount;
        int controlViolations = violationCount - relevantViolations;
        long genericOrderings = Factorial(candidateCount);
        long guidedOrderings = checked(Factorial(relevantCount) * Factorial(controlCount));
        var curve = new List<SlateDbExpandedBudgetPoint>(candidateCount);
        for (int budget = 1; budget <= candidateCount; budget++)
        {
            long genericDetected = genericOrderings - PrefixSafeOrderingCount(
                candidateCount - violationCount,
                candidateCount,
                budget);
            long guidedDetected = guidedOrderings - GuidedSafeOrderingCount(
                relevantCount,
                controlCount,
                relevantViolations,
                controlViolations,
                budget);
            curve.Add(new SlateDbExpandedBudgetPoint(
                budget,
                genericOrderings,
                genericDetected,
                genericDetected / (double)genericOrderings,
                guidedOrderings,
                guidedDetected,
                guidedDetected / (double)guidedOrderings));
        }

        return new SlateDbExpandedTriggerBudgetReport(
            observation.Version,
            evidence,
            curve,
            violationCount,
            relevantCount,
            evidence.Where(static item => item.ViolatesExpectedReadability).All(static item => item.DependencyRelevant),
            curve.Any(static point => point.GuidedDetectionRate > point.GenericDetectionRate),
            CandidateSetFingerprint,
            "Expanded eight-candidate observer grammar is frozen before execution. Guidance prioritizes the complete clone-reader dependency class and treats every member uniformly; it never selects the historically failing candidate by name.");
    }

    private static long GuidedSafeOrderingCount(int relevant, int controls, int relevantViolations, int controlViolations, int budget)
    {
        if (budget <= relevant)
        {
            return checked(FallingFactorial(relevant - relevantViolations, budget) * Factorial(relevant - budget) * Factorial(controls));
        }
        if (relevantViolations > 0)
        {
            return 0;
        }
        int controlBudget = budget - relevant;
        return checked(Factorial(relevant) * FallingFactorial(controls - controlViolations, controlBudget) * Factorial(controls - controlBudget));
    }

    private static long PrefixSafeOrderingCount(int safe, int total, int budget)
        => checked(FallingFactorial(safe, budget) * Factorial(total - budget));

    private static long FallingFactorial(int value, int count)
    {
        if (count < 0 || count > value)
        {
            return 0;
        }
        long result = 1;
        for (int index = 0; index < count; index++)
        {
            result = checked(result * (value - index));
        }
        return result;
    }

    private static long Factorial(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        long result = 1;
        for (int index = 2; index <= value; index++)
        {
            result = checked(result * index);
        }
        return result;
    }

    private static string Fingerprint(IReadOnlyList<(SlateDbExpandedObserverCandidate Candidate, string Field)> fields)
    {
        string canonical = string.Join(
            "|",
            fields.Select(item => $"{item.Candidate}:{item.Field}:{IsDependencyRelevant(item.Candidate)}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
