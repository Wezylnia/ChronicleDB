using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChronicleDB.BranchCheck;

string mode = args.Length == 0 ? "all" : args[0].Trim().ToLowerInvariant();
if (mode is not ("all" or "synthetic" or "historical" or "matrixone" or "matrixone-identity" or "matrixone-budget" or "slatedb" or "slatedb-budget"))
{
    Console.Error.WriteLine("Usage: ChronicleDB.BranchCheck [all|synthetic|historical|matrixone|matrixone-identity|matrixone-budget|slatedb|slatedb-budget]");
    return 2;
}

if (mode == "matrixone-budget")
{
    try
    {
        TriggerBudgetReport budgetReport = await MatrixOneTriggerBudgetCampaign.ExecuteAsync(
            MatrixOneEnvironment.ReadOptions()).ConfigureAwait(false);
        WriteJson(new
        {
            Mode = mode,
            Image = Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_IMAGE"),
            Report = budgetReport,
        });
        return budgetReport.ExactlyOneViolationRecipe && budgetReport.GuidedRecipeIsViolation ? 0 : 1;
    }
    catch (ExternalAdapterException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

if (mode == "slatedb-budget")
{
    try
    {
        string executable = Environment.GetEnvironmentVariable("BRANCHCHECK_SLATEDB_PROBE")
            ?? throw new ExternalAdapterException("BRANCHCHECK_SLATEDB_PROBE is required for slatedb-budget mode.");
        SlateDbObserverObservation observation = await SlateDbObserverAdapter.ObserveAsync(
            executable,
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        SlateDbTriggerBudgetReport budgetReport = SlateDbTriggerBudgetCampaign.Evaluate(observation);
        WriteJson(new
        {
            Mode = mode,
            ExternalIdentity = executable,
            Report = budgetReport,
        });
        return 0;
    }
    catch (ExternalAdapterException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

if (mode is "matrixone" or "matrixone-identity" or "slatedb")
{
    try
    {
        BranchScenario scenario;
        string? externalIdentity;
        string requiredRelation;
        if (mode == "slatedb")
        {
            string executable = Environment.GetEnvironmentVariable("BRANCHCHECK_SLATEDB_PROBE")
                ?? throw new ExternalAdapterException("BRANCHCHECK_SLATEDB_PROBE is required for slatedb mode.");
            scenario = await SlateDbObserverAdapter.ExecuteAsync(
                executable,
                TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            externalIdentity = executable;
            requiredRelation = "BC.observer-dependency";
        }
        else
        {
            SqlCliOptions options = MatrixOneEnvironment.ReadOptions();
            scenario = mode == "matrixone"
                ? await MatrixOneAutoIncrementAdapter.ExecuteAsync(options).ConfigureAwait(false)
                : await MatrixOneHistoricalIdentityAdapter.ExecuteAsync(options).ConfigureAwait(false);
            externalIdentity = Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_IMAGE");
            requiredRelation = mode == "matrixone" ? "BC.continuation-state" : "BC.temporal-boundary";
        }

        ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);
        BaselineResult observerSmoke = AdversarialBaselineSuite.EvaluateObserverSmoke(scenario);
        bool genericDetected = AdversarialBaselineSuite.AnyGenericBaselineDetected(report, branchGrammar, observerSmoke);
        WriteJson(new
        {
            Mode = mode,
            Backend = scenario.Capabilities.BackendName,
            ExternalIdentity = externalIdentity,
            TraceEvidence = scenario.Frames.Select(static frame => new
            {
                frame.Operation,
                frame.OperationClass,
                BranchOutcome = frame.Branch.Outcome,
                BranchDetail = frame.Branch.Detail,
                ReferenceOutcome = frame.Reference.Outcome,
                ReferenceDetail = frame.Reference.Detail,
            }).ToArray(),
            Report = report,
            BranchGrammarBaseline = branchGrammar,
            ObserverSmokeBaseline = observerSmoke,
            AnyGenericBaselineDetected = genericDetected,
            StrictBranchCheckOnly = report.BranchCheckDetected && !genericDetected,
        });
        return report.Relations.Any(result =>
            string.Equals(result.RelationId, requiredRelation, StringComparison.Ordinal)
            && result.Status != RelationStatus.Inconclusive)
            ? 0
            : 1;
    }
    catch (ExternalAdapterException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

var syntheticScenarios = mode is "all" or "synthetic"
    ? SyntheticCampaign.Create()
    : [];
var historicalCases = mode is "all" or "historical"
    ? HistoricalIssueCampaign.Create()
    : [];

var syntheticReports = syntheticScenarios
    .Select(scenario => new
    {
        Scenario = scenario.Name,
        scenario.ExpectedFailingRelationId,
        Report = BranchCheckRunner.Evaluate(scenario),
        BranchGrammarBaseline = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario),
        ObserverSmokeBaseline = AdversarialBaselineSuite.EvaluateObserverSmoke(scenario),
    })
    .ToArray();

var historicalReports = historicalCases
    .Select(issue =>
    {
        ScenarioReport report = BranchCheckRunner.Evaluate(issue.Scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(issue.Scenario);
        BaselineResult observerSmoke = AdversarialBaselineSuite.EvaluateObserverSmoke(issue.Scenario);
        bool genericDetected = AdversarialBaselineSuite.AnyGenericBaselineDetected(report, branchGrammar, observerSmoke);
        return new
        {
            issue.System,
            issue.IssueNumber,
            issue.Title,
            issue.SourceUrl,
            issue.Disposition,
            issue.EvidenceNote,
            Report = report,
            BranchGrammarBaseline = branchGrammar,
            ObserverSmokeBaseline = observerSmoke,
            AnyGenericBaselineDetected = genericDetected,
            StrictBranchCheckOnly = report.BranchCheckDetected && !genericDetected,
        };
    })
    .ToArray();

var output = new
{
    Mode = mode,
    Synthetic = syntheticReports,
    Historical = historicalReports,
    HistoricalSummary = new
    {
        Cases = historicalReports.Length,
        Systems = historicalReports.Select(static report => report.System).Distinct(StringComparer.Ordinal).Count(),
        BranchCheckDetected = historicalReports.Count(static report => report.Report.BranchCheckDetected),
        GenericBaselineDetected = historicalReports.Count(static report => report.AnyGenericBaselineDetected),
        StrictBranchCheckOnly = historicalReports.Count(static report => report.StrictBranchCheckOnly),
        B0Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B0.creation-values"),
        B1Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B1.creation-visible-state"),
        B2Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B2.generic-state-differential"),
        B3Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B3.generic-recovery"),
        B4Detected = historicalReports.Count(static report => report.BranchGrammarBaseline.Detected),
        B5Detected = historicalReports.Count(static report => report.ObserverSmokeBaseline.Detected),
    },
};

WriteJson(output);

bool syntheticValid = syntheticReports.All(static report =>
{
    if (report.ExpectedFailingRelationId is null)
    {
        return !report.Report.BranchCheckDetected;
    }

    return report.Report.Relations.Any(result =>
        string.Equals(result.RelationId, report.ExpectedFailingRelationId, StringComparison.Ordinal)
        && result.Status == RelationStatus.Fail);
});

bool historicalValid = historicalReports.All(static report => report.Report.BranchCheckDetected);
return syntheticValid && historicalValid ? 0 : 1;

static int CountBaselineDetections(IEnumerable<ScenarioReport> reports, string baselineId)
    => reports.Count(report => report.Baselines.Any(result =>
        string.Equals(result.BaselineId, baselineId, StringComparison.Ordinal)
        && result.Detected));

static void WriteJson<T>(T value)
{
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    jsonOptions.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
}

static class MatrixOneEnvironment
{
    public static SqlCliOptions ReadOptions()
        => new(
            Environment.GetEnvironmentVariable("BRANCHCHECK_SQL_CLIENT") ?? "mysql",
            Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_HOST") ?? "127.0.0.1",
            ParsePort(Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_PORT")),
            Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_USER") ?? "root",
            Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_PASSWORD") ?? "111",
            TimeSpan.FromSeconds(60));

    private static int ParsePort(string? raw)
        => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port > 0
            ? port
            : 6001;
}
