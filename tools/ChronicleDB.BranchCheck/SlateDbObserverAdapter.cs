using System.Diagnostics;
using System.Globalization;

namespace ChronicleDB.BranchCheck;

public sealed record SlateDbObserverObservation(
    string Version,
    int TotalKeys,
    int DbReadableKeys,
    int DbReaderReadableKeys,
    string? ReaderError);

public static class SlateDbObserverOutputParser
{
    public static SlateDbObserverObservation Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = rawLine.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            fields[rawLine[..separator]] = rawLine[(separator + 1)..];
        }

        string version = Required(fields, "version");
        int total = ParseCount(fields, "total");
        int db = ParseCount(fields, "db");
        int reader = ParseCount(fields, "reader");
        fields.TryGetValue("reader_error", out string? readerError);
        return new SlateDbObserverObservation(version, total, db, reader, readerError);
    }

    private static string Required(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ExternalAdapterException($"SlateDB observer probe did not emit required field '{key}'.");

    private static int ParseCount(IReadOnlyDictionary<string, string> fields, string key)
    {
        string raw = Required(fields, key);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
        {
            throw new ExternalAdapterException($"SlateDB observer field '{key}' is not a non-negative integer: '{raw}'.");
        }

        return value;
    }
}

public static class SlateDbObserverAdapter
{
    public static async Task<BranchScenario> ExecuteAsync(
        string executable,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalAdapterException($"Failed to start SlateDB observer probe '{executable}'.");
            }
        }
        catch (Exception exception) when (exception is not ExternalAdapterException)
        {
            throw new ExternalAdapterException(
                $"Failed to start SlateDB observer probe '{executable}': {exception.GetType().Name}: {exception.Message}");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new ExternalAdapterException(
                $"SlateDB observer probe exceeded timeout {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ExternalAdapterException(
                $"SlateDB observer probe exited {process.ExitCode}. stderr: {stderr}");
        }

        SlateDbObserverObservation observation = SlateDbObserverOutputParser.Parse(stdout);
        CanonicalState expected = State(observation.TotalKeys);
        var branchObservers = new Dictionary<string, ObserverObservation>(StringComparer.Ordinal)
        {
            ["Db"] = observation.DbReadableKeys == observation.TotalKeys
                ? Success(expected)
                : Failure(observation.DbReadableKeys, observation.TotalKeys, "Db did not read the complete clone"),
            ["DbReader"] = observation.DbReaderReadableKeys == observation.TotalKeys
                ? Success(expected)
                : Failure(observation.DbReaderReadableKeys, observation.TotalKeys, observation.ReaderError),
        };
        var referenceObservers = new Dictionary<string, ObserverObservation>(StringComparer.Ordinal)
        {
            ["Db"] = Success(expected),
            ["DbReader"] = Success(expected),
        };

        return new BranchScenario(
            "slatedb-live-observer-equivalence",
            BranchCapabilityProfile.Create(
                "SlateDB " + observation.Version,
                equivalentObservers: ["Db", "DbReader"]),
            new BranchBoundary("zero-copy-parent", 0),
            expected,
            expected,
            [
                new TraceFrame(
                    "primary-read",
                    branchObservers["Db"],
                    referenceObservers["Db"],
                    OperationClass: TraceOperationClass.GenericRead),
                new TraceFrame(
                    "observe",
                    branchObservers["Db"],
                    referenceObservers["Db"],
                    branchObservers,
                    referenceObservers,
                    TraceOperationClass.ObserverRead),
            ],
            CreationEvidence: CreationEvidenceKind.None);
    }

    private static CanonicalState State(int totalKeys)
        => CanonicalState.Create(
            [new KeyValuePair<string, string>("readable-keys", totalKeys.ToString(CultureInfo.InvariantCulture))],
            "binary-kv",
            "zero-copy-clone");

    private static ObserverObservation Success(CanonicalState state)
        => new(OutcomeClass.Success, state);

    private static ObserverObservation Failure(int readable, int total, string? detail)
        => new(
            OutcomeClass.NotFound,
            CanonicalState.Create(
                [new KeyValuePair<string, string>("readable-keys", $"{readable.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}")],
                "binary-kv",
                "zero-copy-clone"),
            detail);

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
