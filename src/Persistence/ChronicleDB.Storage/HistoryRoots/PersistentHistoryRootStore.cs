using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.Storage.HistoryRoots;

/// <summary>
/// Database-bound append-only registry for durable historical roots.
/// Root events are fixed-size, checksummed and flushed before the caller can
/// acknowledge a lifecycle transition. Only an incomplete final frame is
/// eligible for automatic truncation during recovery.
/// </summary>
public sealed class PersistentHistoryRootStore : IDisposable
{
    public const string FileName = "chronicle.history-roots";

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly IStorageFaultInjector? _faultInjector;
    private readonly Dictionary<HistoryRootId, HistoryRootStoreRecord> _roots = [];
    private readonly HashSet<HistoryRootId> _seenIds = [];
    private ulong _nextEventSequence;
    private bool _faulted;
    private bool _disposed;

    private PersistentHistoryRootStore(
        FileStream stream,
        HistoryRootStoreHeader header,
        IStorageFaultInjector? faultInjector)
    {
        _stream = stream;
        Header = header;
        _faultInjector = faultInjector;
    }

    public HistoryRootStoreHeader Header { get; }

    public bool IsFaulted
    {
        get
        {
            lock (_gate)
            {
                return _faulted;
            }
        }
    }

    public long FileLength
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _stream.Length;
            }
        }
    }

    public ulong NextEventSequenceValue
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _nextEventSequence;
            }
        }
    }

    public static PersistentHistoryRootStore Open(
        string directory,
        Guid databaseId,
        HistoryId mainHistoryId,
        IStorageFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (databaseId == Guid.Empty || !mainHistoryId.IsValid)
        {
            throw new ArgumentException("A history-root store requires valid database and history identities.");
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, FileName);
        FileStream? stream = null;
        try
        {
            EnsureCreatedAtomically(path, new HistoryRootStoreHeader(databaseId, mainHistoryId));
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.SequentialScan);
            var header = ReadHeader(stream);
            if (header.DatabaseId != databaseId || header.MainHistoryId != mainHistoryId)
            {
                throw new StorageFormatException("History-root metadata identity does not match the database.");
            }

            var store = new PersistentHistoryRootStore(stream, header, faultInjector);
            store.ScanAndRecoverTail();
            return store;
        }
        catch (IOException exception)
        {
            stream?.Dispose();
            throw new StorageException($"Could not open history-root metadata '{path}'.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<HistoryRootStoreRecord> ListAll()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _roots.Values
                .OrderBy(root => root.EventSequence)
                .ToArray();
        }
    }

    public IReadOnlyList<HistoryRootStoreRecord> ListRetaining()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _roots.Values
                .Where(root => root.RootState != 4)
                .OrderBy(root => root.Boundary.Value)
                .ThenBy(root => root.RootId.Value)
                .ToArray();
        }
    }

    public bool TryGet(HistoryRootId rootId, out HistoryRootStoreRecord? root)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _roots.TryGetValue(rootId, out root);
        }
    }

    public void AppendCreate(HistoryRootStoreRecord root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (root.Type != HistoryRootStoreRecordType.Create || root.RootState != 2)
            {
                throw new StorageFormatException("History-root create requires an Active root descriptor.");
            }

            if (_seenIds.Contains(root.RootId))
            {
                throw new StorageException($"History root {root.RootId.Value} has already been used.");
            }

            var record = root with { EventSequence = _nextEventSequence };
            AppendRecord(record);
            try
            {
                ApplyRecoveredRecord(record);
                _nextEventSequence = NextEventSequence(_nextEventSequence);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    public void AppendDelete(HistoryRootId rootId)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (!_roots.TryGetValue(rootId, out var active) || active.RootState == 4)
            {
                throw new StorageException($"History root {rootId.Value} does not exist as an active root.");
            }

            var record = active with
            {
                Type = HistoryRootStoreRecordType.Delete,
                EventSequence = _nextEventSequence,
                RootState = 4,
            };
            AppendRecord(record);
            try
            {
                ApplyRecoveredRecord(record);
                _nextEventSequence = NextEventSequence(_nextEventSequence);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_faulted)
                {
                    _stream.Flush(flushToDisk: true);
                }
            }
            finally
            {
                _stream.Dispose();
                _disposed = true;
            }
        }
    }

    private void AppendRecord(HistoryRootStoreRecord record)
    {
        var encoded = HistoryRootStoreRecordCodec.Encode(record);
        var persistenceTouched = false;
        try
        {
            _faultInjector?.Hit(StorageFaultPoint.BeforeHistoryRootRecordWrite, PageId.Invalid);
            _stream.Position = _stream.Length;
            persistenceTouched = true;
            _stream.Write(encoded);
            _faultInjector?.Hit(StorageFaultPoint.AfterHistoryRootRecordWrite, PageId.Invalid);
            _faultInjector?.Hit(StorageFaultPoint.BeforeHistoryRootFlush, PageId.Invalid);
            _stream.Flush(flushToDisk: true);
            _faultInjector?.Hit(StorageFaultPoint.AfterHistoryRootFlush, PageId.Invalid);
        }
        catch
        {
            if (persistenceTouched)
            {
                _faulted = true;
            }

            throw;
        }
    }

    private void ScanAndRecoverTail()
    {
        var position = (long)HistoryRootStoreHeaderCodec.Size;
        var expectedEventSequence = 1UL;
        while (position < _stream.Length)
        {
            var remaining = _stream.Length - position;
            if (remaining < HistoryRootStoreRecordCodec.RecordSize)
            {
                TruncateTail(position);
                break;
            }

            var encoded = new byte[HistoryRootStoreRecordCodec.RecordSize];
            ReadExactly(_stream, encoded, position);
            var record = HistoryRootStoreRecordCodec.Decode(encoded);
            if (record.EventSequence != expectedEventSequence)
            {
                throw new StorageCorruptionException(
                    $"History-root event sequence is discontinuous: expected {expectedEventSequence}, found {record.EventSequence}.");
            }

            ApplyRecoveredRecord(record);
            expectedEventSequence = NextEventSequence(expectedEventSequence);
            position += HistoryRootStoreRecordCodec.RecordSize;
        }

        _nextEventSequence = expectedEventSequence;
    }

    private void ApplyRecoveredRecord(HistoryRootStoreRecord record)
    {
        switch (record.Type)
        {
            case HistoryRootStoreRecordType.Create:
                if (!_seenIds.Add(record.RootId) || _roots.ContainsKey(record.RootId))
                {
                    throw new StorageCorruptionException("History-root identity is reused in persistent metadata.");
                }

                _roots.Add(record.RootId, record);
                break;
            case HistoryRootStoreRecordType.Delete:
                if (!_roots.TryGetValue(record.RootId, out var active) || active.RootState == 4)
                {
                    throw new StorageCorruptionException("History-root delete references an inactive root.");
                }

                if (active.RootKind != record.RootKind
                    || active.OwnerDatabaseId != record.OwnerDatabaseId
                    || active.HistoryId != record.HistoryId
                    || active.ParentHistoryId != record.ParentHistoryId
                    || active.Boundary != record.Boundary
                    || active.CreatedUnixMilliseconds != record.CreatedUnixMilliseconds)
                {
                    throw new StorageCorruptionException("History-root delete metadata does not match its create record.");
                }

                _roots[record.RootId] = record;
                break;
            default:
                throw new StorageFormatException("History-root record type is not supported.");
        }
    }

    private void TruncateTail(long validLength)
    {
        _stream.SetLength(validLength);
        _stream.Flush(flushToDisk: true);
    }

    private static void EnsureCreatedAtomically(string path, HistoryRootStoreHeader header)
    {
        if (File.Exists(path))
        {
            return;
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".creating";
        try
        {
            using (var temporary = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4 * 1024,
                       options: FileOptions.WriteThrough))
            {
                temporary.Write(HistoryRootStoreHeaderCodec.Encode(header));
                temporary.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another opener won the create race; preserve its canonical file.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static HistoryRootStoreHeader ReadHeader(FileStream stream)
    {
        if (stream.Length < HistoryRootStoreHeaderCodec.Size)
        {
            throw new StorageCorruptionException("History-root store header is truncated.");
        }

        var bytes = new byte[HistoryRootStoreHeaderCodec.Size];
        ReadExactly(stream, bytes, 0);
        return HistoryRootStoreHeaderCodec.Decode(bytes);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, long offset)
    {
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                throw new StorageCorruptionException("History-root metadata ended before the requested bytes were read.");
            }

            read += count;
        }
    }

    private void ThrowIfUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new StorageException(
                "History-root metadata is faulted after an uncertain I/O operation and must be reopened.");
        }
    }

    private void EnsureEventSequenceAvailable()
    {
        if (_nextEventSequence == ulong.MaxValue)
        {
            throw new StorageLimitException("History-root event sequence space is exhausted.");
        }
    }

    private static ulong NextEventSequence(ulong current)
    {
        if (current == ulong.MaxValue)
        {
            throw new StorageCorruptionException("History-root event sequence space is exhausted in persistent metadata.");
        }

        return current + 1;
    }
}
