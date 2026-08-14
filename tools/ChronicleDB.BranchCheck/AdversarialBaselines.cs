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

public static class AdversarialBaselineSuite
{
    private static readonly IBranchBaseline BranchGrammar = new GenericBranchGrammarBaseline();

    public static BaselineResult EvaluateBranchGrammar(BranchScenario scenario)
        => BranchGrammar.Evaluate(scenario);

    public static bool AnyGenericBaselineDetected(ScenarioReport report, BaselineResult branchGrammar)
        => report.GenericBaselineDetected || branchGrammar.Detected;
}
