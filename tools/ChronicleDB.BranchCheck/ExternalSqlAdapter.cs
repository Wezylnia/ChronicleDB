using System.Diagnostics;
using System.Globalization;

namespace ChronicleDB.BranchCheck;

public sealed record SqlCliOptions(
    string Executable,
    string Host,
    int Port,
    string User,
    string Password,
    TimeSpan Timeout);

public sealed record SqlCliResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ExternalAdapterException(string message) : Exception(message);

public sealed class SqlCliProcessRunner
{
    private readonly SqlCliOptions _options;

    public SqlCliProcessRunner(SqlCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.User);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Port);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SQL CLI timeout must be positive.");
        }

        _options = options;
    }

    public async Task<SqlCliResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--protocol=tcp");
        startInfo.ArgumentList.Add($"--host={_options.Host}");
        startInfo.ArgumentList.Add($"--port={_options.Port.ToString(CultureInfo.InvariantCulture)}");
        startInfo.ArgumentList.Add($"--user={_options.User}");
        startInfo.ArgumentList.Add("--batch");
        startInfo.ArgumentList.Add("--skip-column-names");
        startInfo.ArgumentList.Add("--raw");
        startInfo.Environment["MYSQL_PWD"] = _options.Password;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalAdapterException($"Failed to start SQL client '{_options.Executable}'.");
            }
        }
        catch (Exception exception) when (exception is not ExternalAdapterException)
        {
            throw new ExternalAdapterException(
                $"Failed to start SQL client '{_options.Executable}': {exception.GetType().Name}: {exception.Message}");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(sql.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new ExternalAdapterException(
                $"SQL client exceeded timeout {_options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new SqlCliResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record MatrixOneAutoIncrementObservation(
    string ServerVersion,
    string CloneRowsAtCreation,
    string ReferenceRowsAtCreation,
    string CloneNextAtCreation,
    string ReferenceNextAtCreation,
    string CloneInsertedId,
    string ReferenceInsertedId,
    string CloneNextAfterInsert,
    string ReferenceNextAfterInsert);

public static class MatrixOneAutoIncrementOutputParser
{
    public static MatrixOneAutoIncrementObservation Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 9)
        {
            throw new ExternalAdapterException(
                $"MatrixOne continuation probe expected 9 scalar output lines but received {lines.Length}. Output: {output}");
        }

        return new MatrixOneAutoIncrementObservation(
            lines[0],
            lines[1],
            lines[2],
            lines[3],
            lines[4],
            lines[5],
            lines[6],
            lines[7],
            lines[8]);
    }
}

public static class MatrixOneAutoIncrementAdapter
{
    public static async Task<BranchScenario> ExecuteAsync(
        SqlCliOptions options,
        CancellationToken cancellationToken = default)
    {
        string database = "branchcheck_" + Guid.NewGuid().ToString("N")[..12];
        string sql = BuildSql(database);
        var runner = new SqlCliProcessRunner(options);
        SqlCliResult result = await runner.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ExternalAdapterException(
                $"MatrixOne SQL probe failed with exit code {result.ExitCode}. stderr: {result.StandardError}");
        }

        MatrixOneAutoIncrementObservation observation = MatrixOneAutoIncrementOutputParser.Parse(result.StandardOutput);
        var boundary = new BranchBoundary($"{database}.src", 0);
        CanonicalState branchCreation = State(
            observation.CloneRowsAtCreation,
            observation.CloneNextAtCreation,
            continuationToken: null);
        CanonicalState referenceCreation = State(
            observation.ReferenceRowsAtCreation,
            observation.ReferenceNextAtCreation,
            continuationToken: null);
        CanonicalState branchAfter = State(
            observation.CloneRowsAtCreation + "," + observation.CloneInsertedId,
            observation.CloneNextAfterInsert,
            observation.CloneInsertedId);
        CanonicalState referenceAfter = State(
            observation.ReferenceRowsAtCreation + "," + observation.ReferenceInsertedId,
            observation.ReferenceNextAfterInsert,
            observation.ReferenceInsertedId);

        return new BranchScenario(
            "matrixone-live-auto-increment-continuation",
            BranchCapabilityProfile.Create("MatrixOne " + observation.ServerVersion),
            boundary,
            branchCreation,
            referenceCreation,
            [
                new TraceFrame(
                    "continuation",
                    Success(branchAfter),
                    Success(referenceAfter),
                    OperationClass: TraceOperationClass.GenericMutation),
            ],
            CreationEvidence: CreationEvidenceKind.All);
    }

    private static string BuildSql(string database)
        => $"""
           SELECT version();
           DROP DATABASE IF EXISTS {database};
           CREATE DATABASE {database};
           CREATE TABLE {database}.src (id BIGINT AUTO_INCREMENT PRIMARY KEY, v INT);
           INSERT INTO {database}.src(v) VALUES (10), (20), (30);
           CREATE TABLE {database}.clone_t CLONE {database}.src;
           CREATE TABLE {database}.reference_t (id BIGINT AUTO_INCREMENT PRIMARY KEY, v INT);
           INSERT INTO {database}.reference_t(v) VALUES (10), (20), (30);
           SELECT GROUP_CONCAT(id ORDER BY id SEPARATOR ',') FROM {database}.clone_t;
           SELECT GROUP_CONCAT(id ORDER BY id SEPARATOR ',') FROM {database}.reference_t;
           SELECT AUTO_INCREMENT FROM information_schema.tables WHERE table_schema = '{database}' AND table_name = 'clone_t';
           SELECT AUTO_INCREMENT FROM information_schema.tables WHERE table_schema = '{database}' AND table_name = 'reference_t';
           INSERT INTO {database}.clone_t(v) VALUES (40);
           INSERT INTO {database}.reference_t(v) VALUES (40);
           SELECT MAX(id) FROM {database}.clone_t;
           SELECT MAX(id) FROM {database}.reference_t;
           SELECT AUTO_INCREMENT FROM information_schema.tables WHERE table_schema = '{database}' AND table_name = 'clone_t';
           SELECT AUTO_INCREMENT FROM information_schema.tables WHERE table_schema = '{database}' AND table_name = 'reference_t';
           DROP DATABASE {database};
           """;

    private static CanonicalState State(string rows, string nextId, string? continuationToken)
        => CanonicalState.Create(
            [new KeyValuePair<string, string>("rows", rows)],
            "id BIGINT AUTO_INCREMENT PRIMARY KEY; v INT",
            "AUTO_INCREMENT=" + nextId,
            continuationToken);

    private static ObserverObservation Success(CanonicalState state)
        => new(OutcomeClass.Success, state);
}
