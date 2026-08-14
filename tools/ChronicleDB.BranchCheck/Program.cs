using System.Text.Json;
using System.Text.Json.Serialization;
using ChronicleDB.BranchCheck;

string mode = args.Length == 0 ? "all" : args[0].Trim().ToLowerInvariant();
if (mode is not ("all" or "synthetic" or "historical"))
{
    Console.Error.WriteLine("Usage: ChronicleDB.BranchCheck [all|synthetic|historical]");
    return 2;
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
    })
    .ToArray();

var historicalReports = historicalCases
    .Select(issue => new
    {
        issue.System,
        issue.IssueNumber,
        issue.Title,
        issue.SourceUrl,
        issue.Disposition,
        issue.EvidenceNote,
        Report = BranchCheckRunner.Evaluate(issue.Scenario),
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
        GenericBaselineDetected = historicalReports.Count(static report => report.Report.GenericBaselineDetected),
        BranchCheckOnly = historicalReports.Count(static report => report.Report.BranchCheckOnly),
        B0Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B0.creation-values"),
        B1Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B1.creation-visible-state"),
        B2Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B2.generic-state-differential"),
        B3Detected = CountBaselineDetections(historicalReports.Select(static report => report.Report), "B3.generic-recovery"),
    },
};

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
jsonOptions.Converters.Add(new JsonStringEnumConverter());
Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));

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
