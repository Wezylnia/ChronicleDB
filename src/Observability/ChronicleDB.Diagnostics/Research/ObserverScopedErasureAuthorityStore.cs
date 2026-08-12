using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public enum ObserverScopedErasureAuthorityFaultPoint : byte
{
    BeforeWrite = 0,
    AfterWriteBeforeFlush = 1,
    AfterFlushBeforePublish = 2,
    AfterPublish = 3,
}

/// <summary>
/// Research-only immutable persistence prototype for an OSEA descriptor. A complete
/// temporary file is flushed before rename publication; orphan temporary files are
/// never authoritative. Loading verifies the descriptor's canonical semantic hash and
/// therefore fails closed on truncation, corruption, or scope mutation.
///
/// This is deliberately separate from ChronicleDB's production database/recovery files.
/// </summary>
public static class ObserverScopedErasureAuthorityStore
{
    public const string FileName = "chronicle.a8-osea.authority.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Publish(
        string directory,
        ObserverScopedErasureAuthorityDescriptor descriptor,
        Action<ObserverScopedErasureAuthorityFaultPoint>? fault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObserverScopedErasureAuthorityDescriptorCompiler.Validate(descriptor);

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, FileName);
        if (File.Exists(path))
        {
            throw new IOException("An OSEA authority is already published in this research directory.");
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".creating";
        try
        {
            fault?.Invoke(ObserverScopedErasureAuthorityFaultPoint.BeforeWrite);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptions);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       options: FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                fault?.Invoke(ObserverScopedErasureAuthorityFaultPoint.AfterWriteBeforeFlush);
                stream.Flush(flushToDisk: true);
            }
            fault?.Invoke(ObserverScopedErasureAuthorityFaultPoint.AfterFlushBeforePublish);

            File.Move(temporary, path);
            _ = Load(directory);
            fault?.Invoke(ObserverScopedErasureAuthorityFaultPoint.AfterPublish);
            return path;
        }
        finally
        {
            TryDeleteNonAuthoritative(temporary);
        }
    }

    public static ObserverScopedErasureAuthorityDescriptor? TryLoad(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(Path.GetFullPath(directory), FileName);
        return File.Exists(path) ? Load(directory) : null;
    }

    public static ObserverScopedErasureAuthorityDescriptor Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(Path.GetFullPath(directory), FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No published OSEA authority exists.", path);
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var descriptor = JsonSerializer.Deserialize<ObserverScopedErasureAuthorityDescriptor>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Published OSEA authority is empty.");
            ObserverScopedErasureAuthorityDescriptorCompiler.Validate(descriptor);
            return descriptor;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Published OSEA authority is not valid canonical JSON.", exception);
        }
    }

    private static void TryDeleteNonAuthoritative(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A .creating file is never authoritative; later cleanup may retry.
        }
    }
}
