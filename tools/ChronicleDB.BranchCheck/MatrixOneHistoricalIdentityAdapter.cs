namespace ChronicleDB.BranchCheck;

public enum MatrixOneIdentityMutationRecipe
{
    NoOp,
    UpdateSourceRow,
    CreateUnrelatedObject,
    RecreateUnrelatedObject,
    RecreateSourceSameName,
}

public sealed record MatrixOneHistoricalIdentityObservation(
    string ServerVersion,
    string SnapshotParentId,
    string CurrentParentId,
    string ChildRow,
    string BranchParentId,
    string ProtectionSnapshotObjectId);

public static class MatrixOneHistoricalIdentityOutputParser
{
    public static MatrixOneHistoricalIdentityObservation Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 6)
        {
            throw new ExternalAdapterException(
                $"MatrixOne historical identity probe expected 6 scalar output lines but received {lines.Length}. Output: {output}");
        }

        return new MatrixOneHistoricalIdentityObservation(
            lines[0],
            lines[1],
            lines[2],
            lines[3],
            lines[4],
            lines[5]);
    }
}

public static class MatrixOneHistoricalIdentityAdapter
{
    public static Task<BranchScenario> ExecuteAsync(
        SqlCliOptions options,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(options, MatrixOneIdentityMutationRecipe.RecreateSourceSameName, cancellationToken);

    public static async Task<BranchScenario> ExecuteAsync(
        SqlCliOptions options,
        MatrixOneIdentityMutationRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string database = "bc_identity_" + suffix;
        string snapshot = "bc_identity_sp_" + suffix;
        var runner = new SqlCliProcessRunner(options);

        SqlCliResult setup = await runner.ExecuteAsync(BuildSetupSql(database, snapshot, recipe), cancellationToken).ConfigureAwait(false);
        if (setup.ExitCode != 0)
        {
            throw new ExternalAdapterException(
                $"MatrixOne historical identity setup ({recipe}) failed with exit code {setup.ExitCode}. stderr: {setup.StandardError}");
        }

        MatrixOneHistoricalIdentityObservation observation = MatrixOneHistoricalIdentityOutputParser.Parse(setup.StandardOutput);
        SqlCliResult diff;
        try
        {
            diff = await runner.ExecuteAsync(BuildDiffSql(database), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runner.ExecuteAsync(BuildCleanupSql(database, snapshot), cancellationToken).ConfigureAwait(false);
        }

        var declared = Boundary(observation.SnapshotParentId);
        var current = Boundary(observation.CurrentParentId);
        var metadataBoundary = Boundary(observation.BranchParentId);
        var dependencyBoundary = Boundary(observation.ProtectionSnapshotObjectId);
        BranchBoundary dataBoundary = string.Equals(observation.ChildRow, "1:snapshot-row", StringComparison.Ordinal)
            ? declared
            : current;

        var branchBoundaries = new Dictionary<string, BranchBoundary>(StringComparer.Ordinal)
        {
            ["data"] = dataBoundary,
            ["metadata"] = metadataBoundary,
            ["dependencies"] = dependencyBoundary,
        };
        CanonicalState branchCreation = State(observation.ChildRow, branchBoundaries);
        CanonicalState referenceCreation = State("1:snapshot-row", componentBoundaries: null);
        string diffDetail = FormatProcessEvidence(diff.StandardOutput, diff.StandardError);
        var diffObservation = new ObserverObservation(
            diff.ExitCode == 0 ? OutcomeClass.Success : OutcomeClass.Rejected,
            null,
            diffDetail);

        return new BranchScenario(
            $"matrixone-live-historical-identity-{recipe}",
            BranchCapabilityProfile.Create(
                "MatrixOne " + observation.ServerVersion,
                supportsHistoricalFork: true,
                sourceBoundaryComponents: ["data", "metadata", "dependencies"]),
            declared,
            branchCreation,
            referenceCreation,
            [
                new TraceFrame(
                    "read-child",
                    Success(branchCreation),
                    Success(referenceCreation),
                    OperationClass: TraceOperationClass.GenericRead),
                new TraceFrame(
                    "data-branch-diff",
                    diffObservation,
                    new ObserverObservation(OutcomeClass.Success, null, "Reference branch-history operation is expected to succeed."),
                    OperationClass: TraceOperationClass.BranchSpecificHistory),
            ],
            CreationEvidence: CreationEvidenceKind.Values | CreationEvidenceKind.Schema);
    }

    private static string BuildSetupSql(
        string database,
        string snapshot,
        MatrixOneIdentityMutationRecipe recipe)
    {
        string mutationSql = BuildMutationSql(database, recipe);
        return $$"""
                 SELECT version();
                 DROP SNAPSHOT IF EXISTS {{snapshot}};
                 DROP DATABASE IF EXISTS {{database}};
                 CREATE DATABASE {{database}};
                 CREATE TABLE {{database}}.parent_t (id INT PRIMARY KEY, val VARCHAR(20));
                 INSERT INTO {{database}}.parent_t VALUES (1, 'snapshot-row');
                 CREATE SNAPSHOT {{snapshot}} FOR TABLE {{database}} parent_t;
                 SELECT rel_id FROM mo_catalog.mo_tables {snapshot='{{snapshot}}'}
                   WHERE account_id = 0 AND reldatabase = '{{database}}' AND relname = 'parent_t';
                 {{mutationSql}}
                 SELECT rel_id FROM mo_catalog.mo_tables
                   WHERE account_id = 0 AND reldatabase = '{{database}}' AND relname = 'parent_t';
                 DATA BRANCH CREATE TABLE {{database}}.child_t
                   FROM {{database}}.parent_t{snapshot='{{snapshot}}'};
                 SELECT CONCAT(id, ':', val) FROM {{database}}.child_t ORDER BY id;
                 SELECT bm.p_table_id
                   FROM mo_catalog.mo_branch_metadata bm
                   JOIN mo_catalog.mo_tables mt ON mt.rel_id = bm.table_id
                   WHERE mt.reldatabase = '{{database}}' AND mt.relname = 'child_t';
                 SELECT obj_id FROM mo_catalog.mo_snapshots
                   WHERE kind = 'branch' AND database_name = '{{database}}' AND table_name = 'parent_t'
                   ORDER BY ts DESC LIMIT 1;
                 """;
    }

    private static string BuildMutationSql(string database, MatrixOneIdentityMutationRecipe recipe)
        => recipe switch
        {
            MatrixOneIdentityMutationRecipe.NoOp => "SET @branchcheck_noop = 1;",
            MatrixOneIdentityMutationRecipe.UpdateSourceRow =>
                $"UPDATE {database}.parent_t SET val = 'current-row' WHERE id = 1;",
            MatrixOneIdentityMutationRecipe.CreateUnrelatedObject =>
                $"CREATE TABLE {database}.unrelated_t (id INT PRIMARY KEY);",
            MatrixOneIdentityMutationRecipe.RecreateUnrelatedObject =>
                $"CREATE TABLE {database}.unrelated_t (id INT PRIMARY KEY); DROP TABLE {database}.unrelated_t; CREATE TABLE {database}.unrelated_t (id INT PRIMARY KEY);",
            MatrixOneIdentityMutationRecipe.RecreateSourceSameName =>
                $"DROP TABLE {database}.parent_t; CREATE TABLE {database}.parent_t (id INT PRIMARY KEY, val VARCHAR(20)); INSERT INTO {database}.parent_t VALUES (2, 'current-row');",
            _ => throw new ArgumentOutOfRangeException(nameof(recipe), recipe, "Unknown MatrixOne identity mutation recipe."),
        };

    private static string BuildDiffSql(string database)
        => $"DATA BRANCH DIFF `{database}`.`child_t` AGAINST `{database}`.`parent_t`;";

    private static string BuildCleanupSql(string database, string snapshot)
        => $"DROP SNAPSHOT IF EXISTS {snapshot}; DROP DATABASE IF EXISTS `{database}`;";

    private static BranchBoundary Boundary(string objectId)
        => new("object:" + objectId, 0);

    private static CanonicalState State(
        string row,
        IReadOnlyDictionary<string, BranchBoundary>? componentBoundaries)
        => CanonicalState.Create(
            [new KeyValuePair<string, string>("parent_t", row)],
            "parent_t(id INT PRIMARY KEY, val VARCHAR(20))",
            "ordinary-table-visible-state",
            componentBoundaries: componentBoundaries);

    private static ObserverObservation Success(CanonicalState state)
        => new(OutcomeClass.Success, state);

    private static string FormatProcessEvidence(string stdout, string stderr)
    {
        string normalizedStdout = Normalize(stdout);
        string normalizedStderr = Normalize(stderr);
        return $"stdout=[{normalizedStdout}] stderr=[{normalizedStderr}]";
    }

    private static string Normalize(string value)
        => string.Join(
            " | ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
