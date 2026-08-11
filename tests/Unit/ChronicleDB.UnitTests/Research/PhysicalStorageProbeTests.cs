using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class PhysicalStorageProbeTests
{
    [Fact]
    public void CaptureSeparatesLogicalAndAllocatedBytes()
    {
        using var directory = TemporaryDirectory.Create();
        File.WriteAllBytes(Path.Combine(directory.Path, "chronicle.wal"), new byte[8_192]);
        File.WriteAllBytes(Path.Combine(directory.Path, "history.checkpoint"), new byte[4_096]);

        var snapshot = PhysicalStorageProbe.Capture(directory.Path);

        Assert.Equal(12_288, snapshot.LogicalLengthBytes);
        Assert.True(snapshot.AllocatedBytes >= 0);
        Assert.Equal(8_192, snapshot.WalBytes);
        Assert.Equal(4_096, snapshot.CheckpointBytes);
        Assert.Equal(2, snapshot.Files.Count);
    }

    [Fact]
    public void MeasureReportsReleasedBytesWithoutChangingTheAction()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "payload.bin");
        File.WriteAllBytes(path, new byte[64 * 1024]);

        var measurement = PhysicalStorageProbe.Measure(
            directory.Path,
            () => File.Delete(path),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(64 * 1024, measurement.LogicalFileLengthReduction);
        Assert.True(measurement.AllocatedFilesystemBytesReduction >= 0);
        Assert.True(measurement.PeakAllocatedBytes >= measurement.Before.AllocatedBytes);
        Assert.True(measurement.Elapsed >= TimeSpan.Zero);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronicle-research-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
