using ChronicleDB.Core.Keys;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;

namespace ChronicleDB;

/// <summary>
/// Supported v0.1 embedded key-value surface. Transactional operations are introduced in v0.2.
/// </summary>
public sealed class ChronicleDatabase : IDisposable
{
    private readonly PersistentKeyValueStore _store;

    private ChronicleDatabase(PersistentKeyValueStore store)
    {
        _store = store;
    }

    public Guid DatabaseId => _store.DatabaseId;

    public int Count => _store.Count;

    public static ChronicleDatabase Open(
        string directory,
        StorageOptions? options = null)
        => new(PersistentKeyValueStore.Open(directory, options));

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        => _store.Put(new BinaryKey(key), value);

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
        => _store.TryGet(new BinaryKey(key), out value);

    public bool Delete(ReadOnlySpan<byte> key)
        => _store.Delete(new BinaryKey(key));

    public void Flush() => _store.Flush();

    public void Dispose() => _store.Dispose();
}
