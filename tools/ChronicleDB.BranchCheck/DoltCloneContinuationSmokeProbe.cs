using System.Net;
using System.Net.Sockets;

using System.Diagnostics;

namespace ChronicleDB.BranchCheck;

public sealed record DoltCloneContinuationSmokeReport(
    string BackendVersion,
    OutcomeClass ContinuationOutcome,
    string? GeneratedId,
    string Detail,
    int PostAttemptReadableRowCount,
    string? PostAttemptReadableMaxPrimaryKey,
    RelationStatus ContinuationRelation,
    string RelationEvidence,
    BaselineStatus CloneGrammarBaseline,
    string CloneGrammarEvidence,
    int ContinuationDelayMilliseconds,
    long ElapsedMilliseconds,
    string ServerProcessHealth);

public static class DoltCloneContinuationSmokeProbe
{
    public static async Task<DoltCloneContinuationSmokeReport> ExecuteAsync(
        DoltCliOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string root = Path.Combine(Path.GetTempPath(), "branchcheck-dolt-clone-smoke-" + Guid.NewGuid().ToString("N"));
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
            var client = new DoltServerClient(options, source, port, "source");
            await client.WaitUntilReadyAsync(server, cancellationToken).ConfigureAwait(false);

            string remoteUrl = FileUrl(remote);
            string cloneQuery = $"""
                USE `source`;
                CALL DOLT_REMOTE('add', 'origin', '{EscapeSql(remoteUrl)}');
                CREATE TABLE test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT);
                CALL DOLT_COMMIT('-Am', 'initial empty table');
                CALL DOLT_PUSH('origin', 'main');
                CALL DOLT_CLONE('{EscapeSql(remoteUrl)}', 'other');
                """;
            await client.RequireSqlAsync(cloneQuery, cancellationToken).ConfigureAwait(false);

            // Deliberately execute the continuation in a separate request. If clone-time
            // global-state initialization incorrectly captures the DOLT_CLONE request
            // context, that request is now complete and its context may be cancelled.
            int continuationDelayMilliseconds = ReadContinuationDelayMilliseconds();
            if (continuationDelayMilliseconds > 0)
            {
                await Task.Delay(continuationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            DoltCliResult insert = await client.ExecuteSqlAsync(
                "USE `other`; INSERT INTO test(v) VALUES (99);",
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            OutcomeClass outcome = insert.ExitCode == 0 ? OutcomeClass.Success : OutcomeClass.Rejected;
            string detail = Normalize(insert.StandardError.Length == 0 ? insert.StandardOutput : insert.StandardError);

            // Do this only after the continuation attempt so the ordinary read does not
            // give asynchronous sequence initialization extra time before the race trigger.
            // A successful read after a rejected AUTO_INCREMENT write shows the destination
            // database itself is still addressable and narrows the failure to latent
            // continuation authority rather than generic clone unusability.
            (int readableRows, long? readableMaxPk) = await client.ReadStateAsync("other", cancellationToken).ConfigureAwait(false);
            string? generatedId = insert.ExitCode == 0
                ? readableMaxPk?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

            string version = (await RequireCliAsync(cli, root, ["version"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            BranchScenario scenario = CreateScenario(version, outcome, generatedId, detail);
            RelationResult relation = new ContinuationStateRelation().Evaluate(scenario);
            BaselineResult cloneGrammar = AdversarialBaselineSuite.EvaluateBranchGrammar(scenario);
            return new DoltCloneContinuationSmokeReport(
                version,
                outcome,
                generatedId,
                detail,
                readableRows,
                readableMaxPk?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                relation.Status,
                relation.Evidence,
                cloneGrammar.Status,
                cloneGrammar.Evidence,
                continuationDelayMilliseconds,
                stopwatch.ElapsedMilliseconds,
                server.HasExited ? "exited-before-return" : "running-after-continuation");
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
        string version,
        OutcomeClass outcome,
        string? generatedId,
        string detail)
    {
        CanonicalState creation = CanonicalState.Create(
            [new KeyValuePair<string, string>("row-count", "0")],
            "test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT)",
            "fresh-clone-visible-state");
        CanonicalState? branchAfter = generatedId is null
            ? null
            : CanonicalState.Create(
                [new KeyValuePair<string, string>("row-count", "1")],
                "test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT)",
                "fresh-clone-visible-state",
                generatedId);
        CanonicalState referenceAfter = CanonicalState.Create(
            [new KeyValuePair<string, string>("row-count", "1")],
            "test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT)",
            "fresh-clone-visible-state",
            "1");

        return new BranchScenario(
            "dolt-clone-separate-request-continuation-smoke",
            BranchCapabilityProfile.Create("Dolt provider " + version),
            new BranchBoundary("other/current-history", 0),
            creation,
            creation,
            [
                new TraceFrame(
                    "clone",
                    new ObserverObservation(OutcomeClass.Success, creation, "DOLT_CLONE returned success and the empty cloned table is addressable."),
                    new ObserverObservation(OutcomeClass.Success, creation, "Reference clone/materialization is complete."),
                    OperationClass: TraceOperationClass.BranchSpecificHistory),
                new TraceFrame(
                    "continuation",
                    new ObserverObservation(outcome, branchAfter, detail),
                    new ObserverObservation(OutcomeClass.Success, referenceAfter, "A fresh cloned AUTO_INCREMENT table accepts its first generated insert as id 1."),
                    OperationClass: TraceOperationClass.GenericMutation),
            ],
            CreationEvidence: CreationEvidenceKind.Values | CreationEvidenceKind.Schema);
    }

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

    private static string Normalize(string value)
        => string.Join(" | ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static int ReadContinuationDelayMilliseconds()
    {
        string? raw = Environment.GetEnvironmentVariable("BRANCHCHECK_DOLT_CONTINUATION_DELAY_MS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int delay)
            || delay < 0)
        {
            throw new ExternalAdapterException($"BRANCHCHECK_DOLT_CONTINUATION_DELAY_MS is invalid: '{raw}'.");
        }

        return delay;
    }
}
