using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ChronicleDB.Diagnostics.Research;

return args.Length == 0
    ? Usage()
    : args[0].ToLowerInvariant() switch
    {
        "authority-crash-campaign" => await RunAuthorityCrashCampaignAsync(args[1..]),
        "authority-child" => RunAuthorityChild(args[1..]),
        _ => Usage(),
    };

static int Usage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  authority-crash-campaign <output-directory>");
    Console.Error.WriteLine("  authority-child <authority-directory> <fault-point> <marker-path>");
    return 2;
}

static async Task<int> RunAuthorityCrashCampaignAsync(string[] args)
{
    if (args.Length != 1)
    {
        return Usage();
    }

    var outputDirectory = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(outputDirectory);
    var expected = BuildDescriptor();
    var failures = 0;

    foreach (var point in Enum.GetValues<ObserverScopedErasureAuthorityFaultPoint>())
    {
        var runDirectory = Path.Combine(outputDirectory, point.ToString());
        if (Directory.Exists(runDirectory))
        {
            Directory.Delete(runDirectory, recursive: true);
        }
        Directory.CreateDirectory(runDirectory);
        var authorityDirectory = Path.Combine(runDirectory, "authority");
        var markerPath = Path.Combine(runDirectory, "crash-marker.txt");

        using var child = StartChild(authorityDirectory, point, markerPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(child);
            Console.Error.WriteLine($"FAIL {point}: child exceeded timeout.");
            failures++;
            continue;
        }

        var markerMatches = File.Exists(markerPath)
            && string.Equals(File.ReadAllText(markerPath), MarkerText(point), StringComparison.Ordinal);
        if (child.ExitCode == 0 || !markerMatches)
        {
            Console.Error.WriteLine(
                $"FAIL {point}: exit={child.ExitCode} marker={(markerMatches ? "matched" : "missing-or-invalid")}.");
            failures++;
            continue;
        }

        var finalPath = Path.Combine(authorityDirectory, ObserverScopedErasureAuthorityStore.FileName);
        var temporaryCount = Directory.Exists(authorityDirectory)
            ? Directory.EnumerateFiles(authorityDirectory, "*.creating", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var finalExists = File.Exists(finalPath);
        var authoritative = ObserverScopedErasureAuthorityStore.TryLoad(authorityDirectory);
        var shouldBePublished = point == ObserverScopedErasureAuthorityFaultPoint.AfterPublish;
        var valid = shouldBePublished
            ? finalExists
                && authoritative is not null
                && string.Equals(authoritative.CanonicalSha256, expected.CanonicalSha256, StringComparison.Ordinal)
            : !finalExists && authoritative is null;

        Console.WriteLine(
            $"{(valid ? "PASS" : "FAIL")} {point}: exit={child.ExitCode} final={finalExists} " +
            $"temporary={temporaryCount} authoritative={(authoritative is null ? "none" : authoritative.CanonicalSha256)}");
        if (!valid)
        {
            failures++;
        }
    }

    Console.WriteLine(
        $"A8 authority crash campaign {(failures == 0 ? "PASS" : "FAIL")} " +
        $"faults={Enum.GetValues<ObserverScopedErasureAuthorityFaultPoint>().Length} failures={failures} " +
        $"scope={expected.CanonicalSha256} output={outputDirectory}");
    return failures == 0 ? 0 : 1;
}

static int RunAuthorityChild(string[] args)
{
    if (args.Length != 3
        || !Enum.TryParse<ObserverScopedErasureAuthorityFaultPoint>(args[1], ignoreCase: true, out var point))
    {
        return Usage();
    }

    var directory = Path.GetFullPath(args[0]);
    var markerPath = Path.GetFullPath(args[2]);
    try
    {
        _ = ObserverScopedErasureAuthorityStore.Publish(
            directory,
            BuildDescriptor(),
            reached =>
            {
                if (reached != point)
                {
                    return;
                }

                WriteCrashMarker(markerPath, reached);
                Environment.FailFast($"Injected A8 authority publication crash at {reached}.");
            });
        Console.Error.WriteLine($"Configured A8 authority fault point {point} was not reached.");
        return 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"A8 authority child failed before FailFast injection: {exception}");
        return 1;
    }
}

static Process StartChild(
    string authorityDirectory,
    ObserverScopedErasureAuthorityFaultPoint point,
    string markerPath)
{
    var start = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
    if (processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    }
    start.ArgumentList.Add("authority-child");
    start.ArgumentList.Add(authorityDirectory);
    start.ArgumentList.Add(point.ToString());
    start.ArgumentList.Add(markerPath);

    var process = Process.Start(start)
        ?? throw new InvalidOperationException("Could not start A8 authority crash child process.");
    _ = Task.Run(async () =>
    {
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            Console.Write(standardOutput);
        }
    });
    _ = Task.Run(async () =>
    {
        var standardError = await process.StandardError.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            Console.Error.Write(standardError);
        }
    });
    return process;
}

static void WriteCrashMarker(string markerPath, ObserverScopedErasureAuthorityFaultPoint point)
{
    Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
    using var stream = new FileStream(
        markerPath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.WriteThrough);
    using var writer = new StreamWriter(stream, leaveOpen: true);
    writer.Write(MarkerText(point));
    writer.Flush();
    stream.Flush(flushToDisk: true);
}

static string MarkerText(ObserverScopedErasureAuthorityFaultPoint point)
    => $"fault={point}";

static void TryKill(Process process)
{
    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException)
    {
    }
}

static ObserverScopedErasureAuthorityDescriptor BuildDescriptor()
{
    var history = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    var snapshotId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    var retention = new ResearchRetentionSnapshot(
        [new ResearchHistoryRetentionSnapshot(
            history,
            1,
            2,
            [
                new ResearchCommittedVersionSnapshot(
                    "v1",
                    Guid.Parse("00000000-0000-0000-0000-0000000000a3"),
                    1,
                    "K",
                    8,
                    32,
                    false),
                new ResearchCommittedVersionSnapshot(
                    "v2",
                    Guid.Parse("00000000-0000-0000-0000-0000000000a4"),
                    2,
                    "K",
                    8,
                    0,
                    true),
            ])],
        [new ResearchPersistentRetentionRootSnapshot(snapshotId, "PersistentSnapshot", history, history, 1)],
        []);
    var closure = new ErasureClosureInput(
        "K",
        history,
        [new ErasureHistoryNode(history, null)],
        [new ErasureRepresentation(
            "wal",
            ErasureRepresentationKind.WalMutation,
            history,
            history,
            1,
            ErasureContentState.Value,
            false)],
        PhysicalRepresentationScanComplete: true,
        []);
    var plan = ObserverExactErasureContractPlanner.Plan(
        retention,
        closure,
        ErasureMode.Force,
        forceAuthorized: true);
    return ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan);
}
