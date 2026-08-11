using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChronicleDB.Diagnostics.Research;

public sealed record ResearchStorageFileSnapshot(
    string RelativePath,
    long LogicalLengthBytes,
    long AllocatedBytes,
    bool AllocationIsExact);

public sealed record ResearchPhysicalStorageSnapshot(
    string Directory,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ResearchStorageFileSnapshot> Files,
    long LogicalLengthBytes,
    long AllocatedBytes,
    long WalBytes,
    long CheckpointBytes,
    bool AllocationIsExact);

public sealed record ResearchPhysicalReclamationMeasurement(
    ResearchPhysicalStorageSnapshot Before,
    ResearchPhysicalStorageSnapshot After,
    long PeakAllocatedBytes,
    TimeSpan Elapsed)
{
    public long LogicalFileLengthReduction => Math.Max(0, Before.LogicalLengthBytes - After.LogicalLengthBytes);

    public long AllocatedFilesystemBytesReduction => Math.Max(0, Before.AllocatedBytes - After.AllocatedBytes);

    public long WalBytesReduction => Math.Max(0, Before.WalBytes - After.WalBytes);

    public long CheckpointBytesReduction => Math.Max(0, Before.CheckpointBytes - After.CheckpointBytes);

    public long PeakTemporaryBytes => Math.Max(0, PeakAllocatedBytes - Before.AllocatedBytes);

    public double ReclamationEfficiency(long bytesRewritten)
        => bytesRewritten <= 0
            ? 0d
            : (double)AllocatedFilesystemBytesReduction / bytesRewritten;
}

/// <summary>
/// Research-only physical storage accounting. Logical file length and allocated
/// filesystem bytes are deliberately reported separately. The probe never
/// participates in retention or reclamation decisions.
/// </summary>
public static class PhysicalStorageProbe
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(2);

    public static ResearchPhysicalStorageSnapshot Capture(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(fullDirectory))
        {
            return new ResearchPhysicalStorageSnapshot(
                fullDirectory,
                DateTimeOffset.UtcNow,
                [],
                LogicalLengthBytes: 0,
                AllocatedBytes: 0,
                WalBytes: 0,
                CheckpointBytes: 0,
                AllocationIsExact: true);
        }

        var files = new List<ResearchStorageFileSnapshot>();
        foreach (var path in System.IO.Directory.EnumerateFiles(fullDirectory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            var (allocatedBytes, exact) = TryGetAllocatedBytes(path, info.Length);
            files.Add(new ResearchStorageFileSnapshot(
                Path.GetRelativePath(fullDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
                info.Length,
                allocatedBytes,
                exact));
        }

        var logical = files.Sum(file => file.LogicalLengthBytes);
        var allocated = files.Sum(file => file.AllocatedBytes);
        var wal = files
            .Where(file => IsWal(file.RelativePath))
            .Sum(file => file.LogicalLengthBytes);
        var checkpoints = files
            .Where(file => IsCheckpoint(file.RelativePath))
            .Sum(file => file.LogicalLengthBytes);
        return new ResearchPhysicalStorageSnapshot(
            fullDirectory,
            DateTimeOffset.UtcNow,
            Array.AsReadOnly(files.ToArray()),
            logical,
            allocated,
            wal,
            checkpoints,
            files.All(file => file.AllocationIsExact));
    }

    public static ResearchPhysicalReclamationMeasurement Measure(
        string directory,
        Action action,
        TimeSpan? sampleInterval = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var interval = sampleInterval ?? TimeSpan.FromMilliseconds(20);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        }

        var before = Capture(directory);
        long peak = before.AllocatedBytes;
        using var cancellation = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var current = Capture(directory);
                    InterlockedExtensions.Max(ref peak, current.AllocatedBytes);
                }
                catch (IOException)
                {
                    // Concurrent replace/delete is expected during compaction. A later
                    // sample or the final capture supplies the authoritative endpoint.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same rationale as IOException: never affect the measured action.
                }

                try
                {
                    await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            stopwatch.Stop();
            cancellation.Cancel();
            try
            {
                sampler.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation after the measured operation.
            }
        }

        var after = Capture(directory);
        peak = Math.Max(peak, after.AllocatedBytes);
        return new ResearchPhysicalReclamationMeasurement(before, after, peak, stopwatch.Elapsed);
    }

    private static bool IsWal(string path)
        => path.EndsWith(".wal", StringComparison.OrdinalIgnoreCase)
            || path.Contains("wal", StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckpoint(string path)
        => path.Contains("checkpoint", StringComparison.OrdinalIgnoreCase);

    private static (long Bytes, bool Exact) TryGetAllocatedBytes(string path, long fallback)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsAllocatedBytes(path, fallback);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxAllocatedBytes(path, fallback);
        }

        return (fallback, false);
    }

    private static (long Bytes, bool Exact) TryGetLinuxAllocatedBytes(string path, long fallback)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "stat",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-c", "%b:%B", path },
            });
            if (process is null)
            {
                return (fallback, false);
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return (fallback, false);
            }

            var parts = output.Split(':');
            if (process.ExitCode == 0
                && parts.Length == 2
                && long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var blocks)
                && long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var blockSize)
                && blocks >= 0
                && blockSize > 0)
            {
                return (checked(blocks * blockSize), true);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or OverflowException)
        {
        }

        return (fallback, false);
    }

    private static (long Bytes, bool Exact) TryGetWindowsAllocatedBytes(string path, long fallback)
    {
        try
        {
            uint high;
            var low = GetCompressedFileSizeW(path, out high);
            if (low == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    return (fallback, false);
                }
            }

            var bytes = ((ulong)high << 32) | low;
            return bytes <= long.MaxValue ? ((long)bytes, true) : (fallback, false);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return (fallback, false);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (value <= current || Interlocked.CompareExchange(ref location, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
