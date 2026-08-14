using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ChronicleDB.BranchCheck;

public static class DoltSqlServerHistoryImportAdapter
{
    public static async Task<BranchScenario> ExecuteAsync(
        DoltCliOptions options,
        DoltHistoryImportRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string root = Path.Combine(Path.GetTempPath(), "branchcheck-dolt-server-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string remote = Path.Combine(root, "remote");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(remote);

        try
        {
            var cli = new DoltCliProcessRunner(options);
            await RequireCliAsync(cli, root, ["config", "--global", "--add", "user.email", "branchcheck@example.invalid"], cancellationToken, allowAlreadyConfigured: true).ConfigureAwait(false);
            await RequireCliAsync(cli, root, ["config", "--global", "--add", "user.name", "BranchCheck"], cancellationToken, allowAlreadyConfigured: true).ConfigureAwait(false);
            await RequireCliAsync(cli, source, ["init"], cancellationToken).ConfigureAwait(false);

            int port = ReserveFreePort();
            string socket = Path.Combine(root, $"dolt-{port}.sock");
            await using var server = await DoltSqlServerProcess.StartAsync(
                options.Executable,
                source,
                port,
                socket,
                options.Timeout,
                cancellationToken).ConfigureAwait(false);
            var client = new DoltServerClient(options, root, port);
            await client.WaitUntilReadyAsync(server, cancellationToken).ConfigureAwait(false);

            string remoteUrl = FileUrl(remote);
            string setupSql = $"""
                CALL DOLT_REMOTE('add', 'origin', '{EscapeSql(remoteUrl)}');
                CREATE TABLE test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT);
                CALL DOLT_COMMIT('-Am', 'initial commit');
                CALL DOLT_PUSH('origin', 'main');
                CALL DOLT_CLONE('{EscapeSql(remoteUrl)}', 'other');
                INSERT INTO test(v) VALUES (1);
                CALL DOLT_COMMIT('-Am', 'insert commit');
                CALL DOLT_PUSH('origin', 'main');
                """;
            await client.RequireSqlAsync(setupSql, cancellationToken).ConfigureAwait(false);

            string recipeSql = BuildRecipeSql(recipe);
            if (!string.IsNullOrWhiteSpace(recipeSql))
            {
                await client.RequireSqlAsync("USE other; " + recipeSql, cancellationToken).ConfigureAwait(false);
            }

            (int rowCount, long? maxPk) = await client.ReadStateAsync("other", cancellationToken).ConfigureAwait(false);
            bool changesRows = DoltHistoryImportSemantics.ChangesCurrentVisibleRows(recipe);
            bool changesSequenceInputs = DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe);
            int expectedRowsBefore = changesRows ? 1 : 0;
            long? expectedVisibleMaxBefore = changesRows ? 1L : null;
            long expectedGlobalSequenceHighWater = changesSequenceInputs ? 1 : 0;

            DoltCliResult insert = await client.ExecuteSqlAsync(
                "USE other; INSERT INTO test(v) VALUES (99);",
                cancellationToken).ConfigureAwait(false);
            OutcomeClass continuationOutcome = insert.ExitCode == 0 ? OutcomeClass.Success : OutcomeClass.Rejected;
            long? maxAfter = null;
            if (insert.ExitCode == 0)
            {
                (_, maxAfter) = await client.ReadStateAsync("other", cancellationToken).ConfigureAwait(false);
            }

            string version = (await RequireCliAsync(cli, root, ["version"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            string detail = Normalize(insert.StandardError.Length == 0 ? insert.StandardOutput : insert.StandardError);
            var observation = new DoltProviderHistoryImportObservation(
                version,
                recipe,
                changesRows,
                changesSequenceInputs,
                rowCount,
                maxPk,
                continuationOutcome,
                maxAfter,
                detail);
            return CreateScenario(
                observation,
                expectedRowsBefore,
                expectedVisibleMaxBefore,
                expectedGlobalSequenceHighWater);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static BranchScenario CreateScenario(
        DoltProviderHistoryImportObservation observation,
        int expectedRowsBefore,
        long? expectedVisibleMaxBefore,
        long expectedGlobalSequenceHighWater)
    {
        ArgumentNullException.ThrowIfNull(observation);
        CanonicalState observedBefore = State(
            observation.RowCountBeforeContinuation,
            observation.MaxPrimaryKeyBeforeContinuation,
            continuationToken: null);
        CanonicalState expectedBefore = State(
            expectedRowsBefore,
            expectedVisibleMaxBefore,
            continuationToken: null);

        long expectedGeneratedId = checked(expectedGlobalSequenceHighWater + 1);
        long expectedVisibleMaxAfter = Math.Max(expectedVisibleMaxBefore ?? long.MinValue, expectedGeneratedId);
        CanonicalState referenceAfter = State(
            expectedRowsBefore + 1,
            expectedVisibleMaxAfter,
            expectedGeneratedId.ToString(CultureInfo.InvariantCulture));
        CanonicalState? branchAfter = observation.MaxPrimaryKeyAfterContinuation is long actualMax
            ? State(
                observation.RowCountBeforeContinuation + 1,
                actualMax,
                actualMax.ToString(CultureInfo.InvariantCulture))
            : null;

        var historyFrame = new TraceFrame(
            "history-import-" + observation.Recipe,
            new ObserverObservation(OutcomeClass.Success, observedBefore),
            new ObserverObservation(OutcomeClass.Success, expectedBefore),
            OperationClass: TraceOperationClass.BranchSpecificHistory);
        var continuationFrame = new TraceFrame(
            "continuation",
            new ObserverObservation(observation.ContinuationOutcome, branchAfter, observation.ContinuationDetail),
            new ObserverObservation(
                OutcomeClass.Success,
                referenceAfter,
                $"Provider-global AUTO_INCREMENT continuation must exceed sequence state imported through local/remote refs; expected id={expectedGeneratedId}."),
            OperationClass: TraceOperationClass.GenericMutation);

        return new BranchScenario(
            "dolt-provider-history-import-" + observation.Recipe,
            BranchCapabilityProfile.Create("Dolt provider " + observation.Version),
            new BranchBoundary("other/current-history", 0),
            observedBefore,
            expectedBefore,
            [historyFrame, continuationFrame],
            CreationEvidence: CreationEvidenceKind.None);
    }

    private static string BuildRecipeSql(DoltHistoryImportRecipe recipe)
        => recipe switch
        {
            DoltHistoryImportRecipe.NoOp => string.Empty,
            DoltHistoryImportRecipe.FetchOnly => "CALL DOLT_FETCH('origin');",
            DoltHistoryImportRecipe.Pull => "CALL DOLT_PULL('origin', 'main');",
            DoltHistoryImportRecipe.FetchMerge => "CALL DOLT_FETCH('origin'); CALL DOLT_MERGE('origin/main');",
            DoltHistoryImportRecipe.FetchHardReset => "CALL DOLT_FETCH('origin'); CALL DOLT_RESET('--hard', 'origin/main');",
            _ => throw new ArgumentOutOfRangeException(nameof(recipe), recipe, "Unknown Dolt history-import recipe."),
        };

    private static async Task<DoltCliResult> RequireCliAsync(
        DoltCliProcessRunner runner,
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowAlreadyConfigured = false)
    {
        DoltCliResult result = await runner.ExecuteAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return result;
        }

        string evidence = Normalize(result.StandardError.Length == 0 ? result.StandardOutput : result.StandardError);
        if (allowAlreadyConfigured && evidence.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        throw new ExternalAdapterException($"Dolt command failed ({string.Join(' ', arguments)}), exit={result.ExitCode}: {evidence}");
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FileUrl(string path)
        => new Uri(Path.GetFullPath(path) + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

    private static string EscapeSql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static CanonicalState State(int rowCount, long? maxPk, string? continuationToken)
        => CanonicalState.Create(
            [
                new KeyValuePair<string, string>("row-count", rowCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("max-pk", maxPk?.ToString(CultureInfo.InvariantCulture) ?? "NULL"),
            ],
            "test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT)",
            "provider-history-visible-state",
            continuationToken);

    private static string Normalize(string value)
        => string.Join(" | ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public sealed record DoltProviderHistoryImportObservation(
    string Version,
    DoltHistoryImportRecipe Recipe,
    bool ChangesCurrentVisibleRows,
    bool ChangesGlobalSequenceInputs,
    int RowCountBeforeContinuation,
    long? MaxPrimaryKeyBeforeContinuation,
    OutcomeClass ContinuationOutcome,
    long? MaxPrimaryKeyAfterContinuation,
    string ContinuationDetail);

internal sealed class DoltServerClient
{
    private readonly DoltCliOptions _options;
    private readonly string _workingDirectory;
    private readonly int _port;

    public DoltServerClient(DoltCliOptions options, string workingDirectory, int port)
    {
        _options = options;
        _workingDirectory = workingDirectory;
        _port = port;
    }

    public async Task WaitUntilReadyAsync(DoltSqlServerProcess server, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (server.HasExited)
            {
                throw new ExternalAdapterException("Dolt sql-server exited during startup: " + await server.ReadLogsAsync().ConfigureAwait(false));
            }
            DoltCliResult result = await ExecuteSqlAsync("SELECT 1;", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        throw new ExternalAdapterException("Timed out waiting for Dolt sql-server: " + await server.ReadLogsAsync().ConfigureAwait(false));
    }

    public async Task<DoltCliResult> RequireSqlAsync(string sql, CancellationToken cancellationToken)
    {
        DoltCliResult result = await ExecuteSqlAsync(sql, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ExternalAdapterException("Dolt server SQL failed: " + result.StandardError + " | " + result.StandardOutput);
        }
        return result;
    }

    public Task<DoltCliResult> ExecuteSqlAsync(string sql, CancellationToken cancellationToken)
    {
        var runner = new DoltCliProcessRunner(_options);
        return runner.ExecuteAsync(
            _workingDirectory,
            [
                "-u", "root",
                "--host", "127.0.0.1",
                "--no-tls",
                "--port", _port.ToString(CultureInfo.InvariantCulture),
                "sql",
                "-r", "csv",
                "-q", sql,
            ],
            cancellationToken);
    }

    public async Task<(int RowCount, long? MaxPk)> ReadStateAsync(string database, CancellationToken cancellationToken)
    {
        DoltCliResult result = await RequireSqlAsync(
            $"USE `{database}`; SELECT COUNT(*) AS row_count, MAX(pk) AS max_pk FROM test;",
            cancellationToken).ConfigureAwait(false);
        string[] lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string[] fields = lines[index].Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 2 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowCount))
            {
                continue;
            }
            if (string.Equals(fields[1], "NULL", StringComparison.OrdinalIgnoreCase) || fields[1].Length == 0)
            {
                return (rowCount, null);
            }
            if (long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long maxPk))
            {
                return (rowCount, maxPk);
            }
        }
        throw new ExternalAdapterException("Could not parse Dolt server row-count/max-pk output: " + result.StandardOutput);
    }
}

internal sealed class DoltSqlServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;

    private DoltSqlServerProcess(Process process)
    {
        _process = process;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public bool HasExited => _process.HasExited;

    public static Task<DoltSqlServerProcess> StartAsync(
        string executable,
        string workingDirectory,
        int port,
        string socket,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _ = timeout;
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("sql-server");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--socket");
        startInfo.ArgumentList.Add(socket);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new ExternalAdapterException("Failed to start Dolt sql-server.");
        }
        return Task.FromResult(new DoltSqlServerProcess(process));
    }

    public async Task<string> ReadLogsAsync()
    {
        if (!_process.HasExited)
        {
            return "server still running";
        }
        return (await _stdout.ConfigureAwait(false)) + " | " + (await _stderr.ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}
