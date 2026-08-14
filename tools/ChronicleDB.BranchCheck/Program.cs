using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using ChronicleDB.BranchCheck;

string mode = args.Length == 0 ? "all" : args[0].Trim().ToLowerInvariant();
if (mode is not ("all" or "synthetic" or "historical" or "matrixone" or "matrixone-identity"))
{
    Console.Error.WriteLine("Usage: ChronicleDB.BranchCheck [all|synthetic|historical|matrixone|matrixone-identity]");
    return 2;
}

if (mode is "matrixone" or "matrixone-identity")
{
    try
    {
        SqlCliOptions options = MatrixOneEnvironment.ReadOptions();
        BranchScenario scenario = mode == "matrixone"
            ? await MatrixOneAutoIncrementAdapter.ExecuteAsync(options).ConfigureAwait(false)
            : await MatrixOneHistoricalIdentityAdapter.ExecuteAsync(options).ConfigureAwait(false);
        ScenarioReport report = BranchCheckRunner.Evaluate(scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);
        WriteJson(new
        {
            Mode = mode,
            Backend = scenario.Capabilities.BackendName,
            Image = Environment.GetEnvironmentVariable("BRANCHCHECK_MATRIXONE_IMAGE"),
            Report = report,
            BranchGrammarBaseline = branchGrammar,
            AnyGenericBaselineDetected = AdversarialBaselineSuite.AnyGenericBaselineDetected(report, branchGrammar),
            StrictBranchCheckOnly = report.BranchCheckDetected
                && !AdversarialBaselineSuite.AnyGenericBaselineDetected(report, branchGrammar),
        });
        string requiredRelation = mode == "matrixone" ? "BC.continuation-state" : "BC.temporal-boundary";
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
    })
    .ToArray();

var historicalReports = historicalCases
    .Select(issue =>
    {
        ScenarioReport report = BranchCheckRunner.Evaluate(issue.Scenario);
        BaselineResult branchGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(issue.Scenario);
        bool genericDetected = AdversarialBaselineSuite.AnyGenericBaselineDetected(report, branchGrammar);
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
