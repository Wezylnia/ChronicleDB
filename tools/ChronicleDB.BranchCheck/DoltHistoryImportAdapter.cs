using System.Diagnostics;
using System.Globalization;

namespace ChronicleDB.BranchCheck;

public enum DoltHistoryImportRecipe
{
    NoOp,
    StatusOnly,
    BranchList,
    LogLocal,
    FetchOnly,
    FetchThenStatus,
    FetchThenBranchList,
    FetchThenLog,
    Pull,
    FetchMerge,
    FetchHardReset,
}

public sealed record DoltHistoryImportObservation(
    string Version,
    DoltHistoryImportRecipe Recipe,
    bool ChangesCurrentVisibleRows,
    bool ChangesGlobalSequenceInputs,
    int RowCountBeforeContinuation,
    long MaxPrimaryKeyBeforeContinuation,
    OutcomeClass ContinuationOutcome,
    long? MaxPrimaryKeyAfterContinuation,
    string ContinuationDetail);

public sealed record DoltCliOptions(
    string Executable,
    TimeSpan Timeout);

public static class DoltHistoryImportSemantics
{
    public static bool ChangesCurrentVisibleRows(DoltHistoryImportRecipe recipe)
        => recipe is DoltHistoryImportRecipe.Pull
            or DoltHistoryImportRecipe.FetchMerge
            or DoltHistoryImportRecipe.FetchHardReset;

    // Dolt's sequence tracker merges state across local branch heads and remote refs.
    // Only recipes that refresh/import origin change the global allocator inputs;
    // local observer commands remain controls in the expanded fair grammar.
    public static bool ChangesGlobalSequenceInputs(DoltHistoryImportRecipe recipe)
        => recipe is DoltHistoryImportRecipe.FetchOnly
            or DoltHistoryImportRecipe.FetchThenStatus
            or DoltHistoryImportRecipe.FetchThenBranchList
            or DoltHistoryImportRecipe.FetchThenLog
            or DoltHistoryImportRecipe.Pull
            or DoltHistoryImportRecipe.FetchMerge
            or DoltHistoryImportRecipe.FetchHardReset;
}

public static class DoltHistoryImportAdapter
{
    public static async Task<BranchScenario> ExecuteAsync(
        DoltCliOptions options,
        DoltHistoryImportRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Executable);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Timeout, TimeSpan.Zero);

        string root = Path.Combine(Path.GetTempPath(), "branchcheck-dolt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runner = new DoltCliProcessRunner(options);
            string source = Path.Combine(root, "source");
            string remote = Path.Combine(root, "remote");
            string candidate = Path.Combine(root, "candidate");
            Directory.CreateDirectory(source);

            await RequireAsync(runner, root, ["config", "--global", "--add", "user.email", "branchcheck@example.invalid"], cancellationToken, allowAlreadyConfigured: true).ConfigureAwait(false);
            await RequireAsync(runner, root, ["config", "--global", "--add", "user.name", "BranchCheck"], cancellationToken, allowAlreadyConfigured: true).ConfigureAwait(false);
            await RequireAsync(runner, source, ["init"], cancellationToken).ConfigureAwait(false);
            await RequireSqlAsync(
                runner,
                source,
                "CREATE TABLE test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT); INSERT INTO test(v) VALUES (10);",
                cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, source, ["add", "."], cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, source, ["commit", "-m", "initial"], cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(remote);
            await RequireAsync(runner, source, ["remote", "add", "origin", FileUrl(remote)], cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, source, ["push", "--set-upstream", "origin", "main"], cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, root, ["clone", FileUrl(remote), "candidate"], cancellationToken).ConfigureAwait(false);

            await RequireSqlAsync(runner, source, "INSERT INTO test(v) VALUES (20);", cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, source, ["add", "."], cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, source, ["commit", "-m", "remote-row"], cancellationToken).ConfigureAwait(false);
            await RequireAsync(runner, source, ["push", "origin", "main"], cancellationToken).ConfigureAwait(false);

            await ExecuteRecipeAsync(runner, candidate, recipe, cancellationToken).ConfigureAwait(false);

            (int rowCount, long maxPk) = await ReadStateAsync(runner, candidate, cancellationToken).ConfigureAwait(false);
            bool changesRows = DoltHistoryImportSemantics.ChangesCurrentVisibleRows(recipe);
            bool changesSequenceInputs = DoltHistoryImportSemantics.ChangesGlobalSequenceInputs(recipe);
            int expectedRowsBefore = changesRows ? 2 : 1;
            long expectedVisibleMaxBefore = changesRows ? 2 : 1;
            long expectedGlobalSequenceHighWater = changesSequenceInputs ? 2 : 1;

            DoltCliResult insert = await runner.ExecuteAsync(
                candidate,
                ["sql", "-q", "INSERT INTO test(v) VALUES (99);"],
                cancellationToken).ConfigureAwait(false);
            OutcomeClass continuationOutcome = insert.ExitCode == 0 ? OutcomeClass.Success : OutcomeClass.Rejected;
            long? maxAfter = null;
            if (insert.ExitCode == 0)
            {
                (_, maxAfter) = await ReadStateAsync(runner, candidate, cancellationToken).ConfigureAwait(false);
            }

            string version = (await RequireAsync(runner, root, ["version"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            string detail = Normalize(insert.StandardError.Length == 0 ? insert.StandardOutput : insert.StandardError);
            var observation = new DoltHistoryImportObservation(
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
        DoltHistoryImportObservation observation,
        int expectedRowsBefore,
        long expectedVisibleMaxBefore,
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
        long expectedVisibleMaxAfter = Math.Max(expectedVisibleMaxBefore, expectedGeneratedId);
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
                $"Global AUTO_INCREMENT continuation must exceed sequence state imported through branch/remote refs; expected id={expectedGeneratedId}."),
            OperationClass: TraceOperationClass.GenericMutation);

        return new BranchScenario(
            "dolt-live-history-import-" + observation.Recipe,
            BranchCapabilityProfile.Create("Dolt " + observation.Version),
            new BranchBoundary("current-history", 0),
            observedBefore,
            expectedBefore,
            [historyFrame, continuationFrame],
            CreationEvidence: CreationEvidenceKind.None);
    }

    private static async Task ExecuteRecipeAsync(
        DoltCliProcessRunner runner,
        string candidate,
        DoltHistoryImportRecipe recipe,
        CancellationToken cancellationToken)
    {
        switch (recipe)
        {
            case DoltHistoryImportRecipe.NoOp:
                return;
            case DoltHistoryImportRecipe.StatusOnly:
                await RequireAsync(runner, candidate, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.BranchList:
                await RequireAsync(runner, candidate, ["branch", "--all"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.LogLocal:
                await RequireAsync(runner, candidate, ["log", "--oneline", "--all", "-n", "2"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.FetchOnly:
                await RequireAsync(runner, candidate, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.FetchThenStatus:
                await RequireAsync(runner, candidate, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
                await RequireAsync(runner, candidate, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.FetchThenBranchList:
                await RequireAsync(runner, candidate, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
                await RequireAsync(runner, candidate, ["branch", "--all"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.FetchThenLog:
                await RequireAsync(runner, candidate, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
                await RequireAsync(runner, candidate, ["log", "--oneline", "--all", "-n", "2"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.Pull:
                await RequireAsync(runner, candidate, ["pull", "origin", "main"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.FetchMerge:
                await RequireAsync(runner, candidate, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
                await RequireAsync(runner, candidate, ["merge", "origin/main"], cancellationToken).ConfigureAwait(false);
                return;
            case DoltHistoryImportRecipe.FetchHardReset:
                await RequireAsync(runner, candidate, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
                await RequireAsync(runner, candidate, ["reset", "--hard", "origin/main"], cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(recipe), recipe, "Unknown Dolt history-import recipe.");
        }
    }

    private static async Task<(int RowCount, long MaxPk)> ReadStateAsync(
        DoltCliProcessRunner runner,
        string directory,
        CancellationToken cancellationToken)
    {
        DoltCliResult result = await RequireAsync(
            runner,
            directory,
            ["sql", "-r", "csv", "-q", "SELECT COUNT(*) AS row_count, MAX(pk) AS max_pk FROM test;"],
            cancellationToken).ConfigureAwait(false);
        string[] lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string[] fields = lines[index].Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length == 2
                && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowCount)
                && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long maxPk))
            {
                return (rowCount, maxPk);
            }
        }

        throw new ExternalAdapterException("Could not parse Dolt row-count/max-pk output: " + Normalize(result.StandardOutput));
    }

    private static Task<DoltCliResult> RequireSqlAsync(
        DoltCliProcessRunner runner,
        string directory,
        string sql,
        CancellationToken cancellationToken)
        => RequireAsync(runner, directory, ["sql", "-q", sql], cancellationToken);

    private static async Task<DoltCliResult> RequireAsync(
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

        throw new ExternalAdapterException(
            $"Dolt command failed ({string.Join(' ', arguments)}), exit={result.ExitCode}: {evidence}");
    }

    private static string FileUrl(string path)
        => new Uri(Path.GetFullPath(path) + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

    private static CanonicalState State(int rowCount, long maxPk, string? continuationToken)
        => CanonicalState.Create(
            [
                new KeyValuePair<string, string>("row-count", rowCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("max-pk", maxPk.ToString(CultureInfo.InvariantCulture)),
            ],
            "test(pk BIGINT PRIMARY KEY AUTO_INCREMENT, v INT)",
            "history-visible-state",
            continuationToken);

    private static string Normalize(string value)
        => string.Join(" | ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public sealed record DoltCliResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class DoltCliProcessRunner
{
    private readonly DoltCliOptions _options;

    public DoltCliProcessRunner(DoltCliOptions options)
    {
        _options = options;
    }

    public async Task<DoltCliResult> ExecuteAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalAdapterException($"Failed to start Dolt executable '{_options.Executable}'.");
            }
        }
        catch (Exception exception) when (exception is not ExternalAdapterException)
        {
            throw new ExternalAdapterException(
                $"Failed to start Dolt executable '{_options.Executable}': {exception.GetType().Name}: {exception.Message}");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
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
                $"Dolt command exceeded timeout {_options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s: {string.Join(' ', arguments)}");
        }

        return new DoltCliResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
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
