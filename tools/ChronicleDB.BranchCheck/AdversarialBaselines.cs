namespace ChronicleDB.BranchCheck;

public sealed class GenericBranchGrammarBaseline : IBranchBaseline
{
    public string Id => "B4.generic-branch-grammar";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        TraceFrame[] eligible = scenario.Frames
            .Where(static frame => frame.OperationClass is TraceOperationClass.BranchSpecificHistory or TraceOperationClass.BranchSpecificLifecycle)
            .ToArray();

        if (eligible.Length == 0)
        {
            return new BaselineResult(
                Id,
                BaselineStatus.NotApplicable,
                "No branch-specific history/lifecycle operation occurs in this trace.");
        }

        foreach (TraceFrame frame in eligible)
        {
            if (frame.Branch.Outcome != frame.Reference.Outcome)
            {
                return new BaselineResult(
                    Id,
                    BaselineStatus.Detected,
                    $"Branch-grammar operation '{frame.Operation}' diverged in outcome: branch={frame.Branch.Outcome}, reference={frame.Reference.Outcome}.");
            }

            if (!Comparison.OrdinaryVisibleStateEqual(frame.Branch.State, frame.Reference.State))
            {
                return new BaselineResult(
                    Id,
                    BaselineStatus.Detected,
                    $"Branch-grammar operation '{frame.Operation}' diverged in ordinary visible state.");
            }
        }

        return new BaselineResult(
            Id,
            BaselineStatus.Pass,
            $"All {eligible.Length} branch-grammar operations match in ordinary outcome/state.");
    }
}

public sealed class GenericObserverSmokeBaseline : IBranchBaseline
{
    public string Id => "B5.generic-observer-smoke";

    public BaselineResult Evaluate(BranchScenario scenario)
    {
        TraceFrame[] observerFrames = scenario.Frames
            .Where(static frame => frame.BranchObservers is not null && frame.ReferenceObservers is not null)
            .ToArray();
        if (observerFrames.Length == 0)
        {
            return new BaselineResult(
                Id,
                BaselineStatus.NotApplicable,
                "No alternate observer evidence occurs in this trace.");
        }

        int compared = 0;
        foreach (TraceFrame frame in observerFrames)
        {
            foreach (KeyValuePair<string, ObserverObservation> observer in frame.BranchObservers!)
            {
                if (!frame.ReferenceObservers!.TryGetValue(observer.Key, out ObserverObservation? reference))
                {
                    continue;
                }

                compared++;
                if (observer.Value.Outcome != reference.Outcome)
                {
                    return new BaselineResult(
                        Id,
                        BaselineStatus.Detected,
                        $"Observer smoke check '{observer.Key}' diverged in outcome: branch={observer.Value.Outcome}, reference={reference.Outcome}.");
                }

                if (!Comparison.OrdinaryVisibleStateEqual(observer.Value.State, reference.State))
                {
                    return new BaselineResult(
                        Id,
                        BaselineStatus.Detected,
                        $"Observer smoke check '{observer.Key}' diverged in ordinary visible state.");
                }
            }
        }

        return compared == 0
            ? new BaselineResult(Id, BaselineStatus.Inconclusive, "Observer frames had no names shared with their reference observations.")
            : new BaselineResult(Id, BaselineStatus.Pass, $"All {compared} alternate observer/reference pairs match in ordinary outcome/state.");
    }
}

public static class AdversarialBaselineSuite
{
    private static readonly GenericBranchGrammarBaseline BranchGrammar = new();
    private static readonly GenericObserverSmokeBaseline ObserverSmoke = new();

    public static BaselineResult EvaluateBranchGrammar(BranchScenario scenario)
        => BranchGrammar.Evaluate(scenario);

    public static BaselineResult EvaluateObserverSmoke(BranchScenario scenario)
        => ObserverSmoke.Evaluate(scenario);

    public static bool AnyGenericBaselineDetected(ScenarioReport report, params BaselineResult[] adversarialBaselines)
        => report.GenericBaselineDetected || adversarialBaselines.Any(static baseline => baseline.Detected);
}
