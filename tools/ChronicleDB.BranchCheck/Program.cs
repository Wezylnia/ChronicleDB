using System.Text.Json;
using ChronicleDB.BranchCheck;

IBranchBaseline[] baselines =
[
    new CreationValuesBaseline(),
    new CreationVisibleStateBaseline(),
];

IBranchRelation[] relations =
[
    new ContinuationStateRelation(),
    new TemporalBoundaryRelation(),
    new LifecycleRelation(),
    new ObserverDependencyRelation(),
];

var reports = SyntheticCampaign.Create()
    .Select(scenario => new
    {
        scenario.Name,
        scenario.ExpectedFailingRelationId,
        Baselines = baselines.Select(baseline => baseline.Evaluate(scenario)).ToArray(),
        Relations = relations.Select(relation => relation.Evaluate(scenario)).ToArray(),
    })
    .ToArray();

var options = new JsonSerializerOptions { WriteIndented = true };
Console.WriteLine(JsonSerializer.Serialize(reports, options));

bool campaignValid = reports.All(report =>
{
    bool creationBaselinesPass = report.Baselines.All(static result => result.Passed);
    if (!creationBaselinesPass)
    {
        return false;
    }

    if (report.ExpectedFailingRelationId is null)
    {
        return report.Relations.All(static result => result.Status is RelationStatus.Pass or RelationStatus.NotApplicable or RelationStatus.Inconclusive);
    }

    return report.Relations.Any(result =>
        string.Equals(result.RelationId, report.ExpectedFailingRelationId, StringComparison.Ordinal)
        && result.Status == RelationStatus.Fail);
});

return campaignValid ? 0 : 1;
