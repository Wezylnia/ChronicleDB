using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.Storage.Snapshots;

/// <summary>
/// Exclusive append-only metadata store for persistent named snapshot lifecycle records.
/// The file is database-bound, checksummed and self-framed; only an incomplete final
/// record may be truncated automatically during open.
/// </summary>
public sealed class PersistentSnapshotStore : IDisposable
{
    public const string FileName = "chronicle.snapshots";

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly IStorageFaultInjector? _faultInjector;
    private readonly Dictionary<SnapshotId, SnapshotStoreRecord> _active = [];
    private readonly Dictionary<string, SnapshotId> _names = new(StringComparer.Ordinal);
    private readonly HashSet<SnapshotId> _seenIds = [];
    private ulong _nextEventSequence;
    private CommitSequence _maximumReferencedSequence;
    private bool _faulted;
    private bool _disposed;

    private PersistentSnapshotStore(
        FileStream stream,
        SnapshotStoreHeader header,
        IStorageFaultInjector? faultInjector,
        ulong nextEventSequence)
    {
        _stream = stream;
        Header = header;
        _faultInjector = faultInjector;
        _nextEventSequence = nextEventSequence;
    }

    public SnapshotStoreHeader Header { get; }

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

    public CommitSequence MaximumReferencedSequence
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _maximumReferencedSequence;
            }
        }
    }

    public static PersistentSnapshotStore Open(
        string directory,
        Guid databaseId,
        CommitSequence initialRetentionFloor,
        IStorageFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (databaseId == Guid.Empty)
        {
            throw new ArgumentException("A snapshot store requires a non-empty database ID.", nameof(databaseId));
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, FileName);
        FileStream? stream = null;
        try
        {
            EnsureCreatedAtomically(path, new SnapshotStoreHeader(databaseId, initialRetentionFloor));
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.SequentialScan);
            var header = ReadHeader(stream);
            if (header.DatabaseId != databaseId)
            {
                throw new StorageFormatException("Snapshot metadata database identity does not match the storage database.");
            }

            var store = new PersistentSnapshotStore(stream, header, faultInjector, nextEventSequence: 1);
            store.ScanAndRecoverTail();
            return store;
        }
        catch (IOException exception)
        {
            stream?.Dispose();
            throw new StorageException($"Could not open snapshot metadata '{path}'.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<SnapshotStoreRecord> ListActive()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _active.Values
                .OrderBy(record => record.Sequence.Value)
                .ThenBy(record => record.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public void AppendCreate(
        SnapshotId snapshotId,
        CommitSequence sequence,
        long createdUnixMilliseconds,
        string name)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (_seenIds.Contains(snapshotId))
            {
                throw new StorageException($"Snapshot ID {snapshotId.Value} has already been used.");
            }

            if (_names.ContainsKey(name))
            {
                throw new StorageException($"Snapshot name '{name}' already exists.");
            }

            if (sequence < Header.RetentionFloor)
            {
                throw new StorageException("Snapshot sequence is older than the persistent retention floor.");
            }

            var record = new SnapshotStoreRecord(
                SnapshotStoreRecordType.Create,
                _nextEventSequence,
                snapshotId,
                sequence,
                createdUnixMilliseconds,
                name);
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

    public void AppendDelete(SnapshotId snapshotId)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (!_active.ContainsKey(snapshotId))
            {
                throw new StorageException($"Snapshot {snapshotId.Value} does not exist.");
            }

            var record = new SnapshotStoreRecord(
                SnapshotStoreRecordType.Delete,
                _nextEventSequence,
                snapshotId,
                CommitSequence.Initial,
                CreatedUnixMilliseconds: 0,
                Name: string.Empty);
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

    private void AppendRecord(SnapshotStoreRecord record)
    {
        var encoded = SnapshotStoreRecordCodec.Encode(record);
        var persistenceTouched = false;
        try
        {
            _faultInjector?.Hit(StorageFaultPoint.BeforeSnapshotRecordWrite, PageId.Invalid);
            _stream.Position = _stream.Length;
            persistenceTouched = true;
            _stream.Write(encoded);
            _faultInjector?.Hit(StorageFaultPoint.AfterSnapshotRecordWrite, PageId.Invalid);
            _faultInjector?.Hit(StorageFaultPoint.BeforeSnapshotFlush, PageId.Invalid);
            _stream.Flush(flushToDisk: true);
            _faultInjector?.Hit(StorageFaultPoint.AfterSnapshotFlush, PageId.Invalid);
        }
        catch
        {
            if (persistenceTouched)
            {
                // Once append may have started, persistence outcome is ambiguous. Do not
                // flush this instance during Dispose; reopen and scan the framed tail.
                _faulted = true;
            }

            throw;
        }
    }

    private void ScanAndRecoverTail()
    {
        var position = (long)SnapshotStoreHeaderCodec.Size;
        var expectedEventSequence = 1UL;
        var headerBuffer = new byte[SnapshotStoreRecordCodec.HeaderSize];
        while (position < _stream.Length)
        {
            var remaining = _stream.Length - position;
            if (remaining < SnapshotStoreRecordCodec.HeaderSize)
            {
                TruncateTail(position);
                break;
            }

            ReadExactly(_stream, headerBuffer, position);
            var repeatedLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(10, 2));
            var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(12, 4));
            if (repeatedLength != totalLength
                || totalLength < SnapshotStoreRecordCodec.MinimumRecordSize
                || totalLength > SnapshotStoreRecordCodec.MaximumRecordSize)
            {
                throw new StorageCorruptionException("Snapshot record frame length is invalid.");
            }

            if (totalLength > remaining)
            {
                if (HasCompleteFooter(position, remaining))
                {
                    throw new StorageCorruptionException(
                        "Snapshot record header length is corrupt even though a complete footer is present.");
                }

                TruncateTail(position);
                break;
            }

            var encoded = new byte[checked((int)totalLength)];
            ReadExactly(_stream, encoded, position);
            var record = SnapshotStoreRecordCodec.Decode(encoded);
            if (record.EventSequence != expectedEventSequence)
            {
                throw new StorageCorruptionException(
                    $"Snapshot event sequence is discontinuous: expected {expectedEventSequence}, found {record.EventSequence}.");
            }

            ApplyRecoveredRecord(record);
            expectedEventSequence = NextEventSequence(expectedEventSequence);
            position += totalLength;
        }

        _nextEventSequence = expectedEventSequence;
    }

    private void ApplyRecoveredRecord(SnapshotStoreRecord record)
    {
        switch (record.Type)
        {
            case SnapshotStoreRecordType.Create:
                if (record.Sequence < Header.RetentionFloor)
                {
                    throw new StorageCorruptionException("Snapshot record predates the retained historical floor.");
                }

                if (!_seenIds.Add(record.SnapshotId))
                {
                    throw new StorageCorruptionException("Snapshot ID is reused in persistent metadata.");
                }

                if (_active.ContainsKey(record.SnapshotId) || _names.ContainsKey(record.Name))
                {
                    throw new StorageCorruptionException("Snapshot create record duplicates an active identity or name.");
                }

                _active.Add(record.SnapshotId, record);
                _names.Add(record.Name, record.SnapshotId);
                if (record.Sequence > _maximumReferencedSequence)
                {
                    _maximumReferencedSequence = record.Sequence;
                }

                break;
            case SnapshotStoreRecordType.Delete:
                if (!_active.Remove(record.SnapshotId, out var removed))
                {
                    throw new StorageCorruptionException("Snapshot delete record references an inactive snapshot.");
                }

                _names.Remove(removed.Name);
                break;
            default:
                throw new StorageFormatException("Snapshot record type is not supported.");
        }
    }

    private bool HasCompleteFooter(long recordOffset, long remaining)
    {
        if (remaining < SnapshotStoreRecordCodec.FooterSize || remaining > int.MaxValue)
        {
            return false;
        }

        var footer = new byte[SnapshotStoreRecordCodec.FooterSize];
        ReadExactly(
            _stream,
            footer,
            checked(recordOffset + remaining - SnapshotStoreRecordCodec.FooterSize));
        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(0, 4));
        return footerLength == remaining && footer.AsSpan(4, 4).SequenceEqual("SEND"u8);
    }

    private void TruncateTail(long validLength)
    {
        _stream.SetLength(validLength);
        _stream.Flush(flushToDisk: true);
    }

    private static void EnsureCreatedAtomically(string path, SnapshotStoreHeader header)
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
                temporary.Write(SnapshotStoreHeaderCodec.Encode(header));
                temporary.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another opener can only win this race before ChronicleDB owns the
                // database files. Preserve the canonical file and discard our temp.
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

    private static SnapshotStoreHeader ReadHeader(FileStream stream)
    {
        if (stream.Length < SnapshotStoreHeaderCodec.Size)
        {
            throw new StorageCorruptionException("Snapshot store header is truncated.");
        }

        var bytes = new byte[SnapshotStoreHeaderCodec.Size];
        ReadExactly(stream, bytes, 0);
        return SnapshotStoreHeaderCodec.Decode(bytes);
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
                throw new StorageCorruptionException("Snapshot metadata ended before the requested bytes were read.");
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
                "Snapshot metadata is faulted after an uncertain I/O operation and must be reopened.");
        }
    }

    private void EnsureEventSequenceAvailable()
    {
        if (_nextEventSequence == ulong.MaxValue)
        {
            throw new StorageLimitException("Snapshot event sequence space is exhausted.");
        }
    }

    private static ulong NextEventSequence(ulong current)
    {
        if (current == ulong.MaxValue)
        {
            throw new StorageCorruptionException("Snapshot event sequence space is exhausted in persistent metadata.");
        }

        return current + 1;
    }
}
