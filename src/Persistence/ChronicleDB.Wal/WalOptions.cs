using ChronicleDB.Wal.Errors;

namespace ChronicleDB.Wal;

public sealed record WalOptions
{
    public const string DefaultFileName = "chronicle.wal";

    public string FileName { get; init; } = DefaultFileName;

    public bool FlushOnAppend { get; init; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FileName);
        if (Path.IsPathRooted(FileName)
            || FileName is "." or ".."
            || FileName.Contains('/')
            || FileName.Contains('\\')
            || FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new WalFormatException("WAL file name must be a single relative file name.");
        }

        if (!FileName.EndsWith(".wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new WalFormatException("WAL file name must use the .wal extension.");
        }
    }
}
