using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.History;

namespace ChronicleDB;

/// <summary>
/// Applies the database's configured logical key/value contract to durable history
/// before that history is admitted into the in-memory MVCC model or used to rebuild
/// derived physical state. Persistent framing formats intentionally have wider
/// absolute envelopes and therefore cannot serve as the logical validation policy.
/// </summary>
internal static class RecoveredLogicalHistoryValidator
{
    public static void ValidateCheckpoint(
        HistoryCheckpoint checkpoint,
        StorageOptions options,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        foreach (var version in checkpoint.Versions)
        {
            ValidateMutation(
                version.Key.Length,
                version.Value.Length,
                version.IsDelete,
                options,
                sourceName);
        }
    }

    public static void ValidateMutations(
        IEnumerable<StorageMutation> mutations,
        StorageOptions options,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        foreach (var mutation in mutations)
        {
            ValidateMutation(
                mutation.Key.Length,
                mutation.Value.Length,
                mutation.IsDelete,
                options,
                sourceName);
        }
    }

    private static void ValidateMutation(
        int keyLength,
        int valueLength,
        bool isDelete,
        StorageOptions options,
        string sourceName)
    {
        if (keyLength > options.MaxKeySize)
        {
            throw new StorageCorruptionException(
                $"{sourceName} contains a key of {keyLength} bytes, exceeding the configured database maximum of {options.MaxKeySize} bytes.");
        }

        if (valueLength > options.MaxValueSize)
        {
            throw new StorageCorruptionException(
                $"{sourceName} contains a value of {valueLength} bytes, exceeding the configured database maximum of {options.MaxValueSize} bytes.");
        }

        if (isDelete && valueLength != 0)
        {
            throw new StorageCorruptionException($"{sourceName} contains a tombstone with a value.");
        }
    }
}
