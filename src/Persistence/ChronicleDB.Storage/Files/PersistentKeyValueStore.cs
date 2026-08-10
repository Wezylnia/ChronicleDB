using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.Pages;
using ChronicleDB.Storage.Records;

namespace ChronicleDB.Storage.Files;

/// <summary>
/// v0.1 append-only persistent key-value store. Transactions and WAL are intentionally not part of this type.
/// </summary>
public sealed class PersistentKeyValueStore : IDisposable
{
    public const string MetadataFileName = "chronicle.meta";
    public const string DataFileName = "chronicle.data";

    private readonly object _gate = new();
    private readonly StorageOptions _options;
    private readonly FileStream _metadata;
    private readonly FileStream _data;
    private readonly bool _allowIncompleteFinalPage;
    private readonly Dictionary<BinaryKey, StoredRecord> _records = [];
    private long? _untrustedTailOffset;
    private bool _untrustedTailIsFinalAppend;
    private bool _untrustedTailIsPartialPage;
    private bool _disposed;

    private PersistentKeyValueStore(
        StorageOptions options,
        FileStream metadata,
        FileStream data,
        DatabaseHeader header,
        bool allowIncompleteFinalPage)
    {
        _options = options;
        _metadata = metadata;
        _data = data;
        _allowIncompleteFinalPage = allowIncompleteFinalPage;
        Header = header;
    }

    public DatabaseHeader Header { get; }

    public Guid DatabaseId => Header.DatabaseId;

    internal bool HasUntrustedTail
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _untrustedTailOffset.HasValue;
            }
        }
    }

    internal long? UntrustedTailOffset
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _untrustedTailOffset;
            }
        }
    }

    internal bool UntrustedTailIsFinalAppend
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _untrustedTailIsFinalAppend;
            }
        }
    }

    internal bool UntrustedTailIsPartialPage
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _untrustedTailIsPartialPage;
            }
        }
    }

    internal long DataLength
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _data.Length;
            }
        }
    }

    internal bool CanRepairFrom(long baseDataLength)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return baseDataLength >= 0
                   && baseDataLength % _options.PageSize == 0
                   && baseDataLength <= _data.Length
                   && _untrustedTailOffset is { } tailOffset
                   && baseDataLength <= tailOffset;
        }
    }

    internal void DiscardUntrustedTail()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_untrustedTailOffset is not { } offset)
            {
                return;
            }

            DiscardUntrustedTailCore(offset);
        }
    }

    internal void DiscardUntrustedTail(long safeBaseDataLength)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!CanRepairFrom(safeBaseDataLength))
            {
                throw new StorageCorruptionException(
                    "The WAL recovery base is not a valid prefix of the untrusted data tail.");
            }

            DiscardUntrustedTailCore(safeBaseDataLength);
        }
    }

    private void DiscardUntrustedTailCore(long offset)
    {
        _data.SetLength(offset);
        _data.Flush(flushToDisk: true);
        _untrustedTailOffset = null;
        _untrustedTailIsFinalAppend = false;
        _untrustedTailIsPartialPage = false;
        _records.Clear();
        ScanDataFile();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _records.Count;
            }
        }
    }

    public static PersistentKeyValueStore Open(
        string directory,
        StorageOptions? options = null)
        => Open(directory, options, allowIncompleteFinalPage: false);

    public static PersistentKeyValueStore Open(
        string directory,
        StorageOptions? options,
        bool allowIncompleteFinalPage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var validatedOptions = options ?? new StorageOptions();
        validatedOptions.Validate();

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);

        FileStream? metadata = null;
        FileStream? data = null;

        try
        {
            metadata = OpenExclusive(Path.Combine(fullDirectory, MetadataFileName));
            data = OpenExclusive(Path.Combine(fullDirectory, DataFileName));
            var header = OpenOrCreateHeader(metadata, data, validatedOptions, allowIncompleteFinalPage);
            var store = new PersistentKeyValueStore(
                validatedOptions,
                metadata,
                data,
                header,
                allowIncompleteFinalPage);
            if (data.Length % validatedOptions.PageSize != 0)
            {
                store._untrustedTailOffset = data.Length - (data.Length % validatedOptions.PageSize);
                store._untrustedTailIsFinalAppend = true;
                store._untrustedTailIsPartialPage = true;
            }

            store.ScanDataFile();
            return store;
        }
        catch
        {
            data?.Dispose();
            metadata?.Dispose();
            throw;
        }
    }

    internal IReadOnlyList<StorageMutation> SnapshotCurrentState()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var snapshot = new List<StorageMutation>(_records.Count);
            foreach (var (key, record) in _records)
            {
                var value = record.InlineValue.Length != 0 || record.ValueLength == 0
                    ? record.InlineValue
                    : ReadOverflow(record.OverflowHead, record.ValueLength);
                snapshot.Add(new StorageMutation(key, isDelete: false, value));
            }

            return snapshot;
        }
    }

    public bool TryGet(BinaryKey key, out byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_records.TryGetValue(key, out var record))
            {
                value = [];
                return false;
            }

            value = record.InlineValue.Length != 0 || record.ValueLength == 0
                ? (byte[])record.InlineValue.Clone()
                : ReadOverflow(record.OverflowHead, record.ValueLength);
            return true;
        }
    }

    public void Put(BinaryKey key, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateKey(key);
            if (value.Length > _options.MaxValueSize)
            {
                throw new StorageLimitException("Value exceeds the configured maximum size.");
            }

            var overflowHead = PageId.Invalid;
            if (value.Length > _options.InlineValueCapacity(key.Length))
            {
                overflowHead = AppendOverflow(value);
            }

            var payload = RecordCodec.Encode(key, value, overflowHead, tombstone: false, _options);
            var pageId = AppendPage(PageType.Record, payload);
            FlushIfConfigured();
            var inlineValue = overflowHead.IsValid ? [] : value.ToArray();
            _records[key] = new StoredRecord(pageId, value.Length, overflowHead, inlineValue);
        }
    }

    public bool Delete(BinaryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateKey(key);
            var existed = _records.ContainsKey(key);
            var payload = RecordCodec.Encode(key, ReadOnlySpan<byte>.Empty, PageId.Invalid, tombstone: true, _options);
            AppendPage(PageType.Record, payload);
            FlushIfConfigured();
            _records.Remove(key);
            return existed;
        }
    }

    /// <summary>
    /// Preflights deterministic storage limits and encodings before a transaction
    /// is allowed to make its durable WAL decision.
    /// </summary>
    public void ValidateBatch(IReadOnlyList<StorageMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);

        lock (_gate)
        {
            ThrowIfDisposed();
            long additionalPages = 0;
            foreach (var mutation in mutations)
            {
                ArgumentNullException.ThrowIfNull(mutation);
                ValidateKey(mutation.Key);
                if (mutation.Value.Length > _options.MaxValueSize)
                {
                    throw new StorageLimitException("Value exceeds the configured maximum size.");
                }

                if (mutation.IsDelete)
                {
                    _ = RecordCodec.Encode(
                        mutation.Key,
                        ReadOnlySpan<byte>.Empty,
                        PageId.Invalid,
                        tombstone: true,
                        _options);
                    additionalPages = checked(additionalPages + 1);
                    continue;
                }

                var usesOverflow = mutation.Value.Length > _options.InlineValueCapacity(mutation.Key.Length);
                if (usesOverflow)
                {
                    var chunkCapacity = checked(_options.PageSize - PageCodec.Size - OverflowCodec.HeaderSize);
                    var overflowPages = checked((mutation.Value.Length + chunkCapacity - 1) / chunkCapacity);
                    additionalPages = checked(additionalPages + overflowPages);
                }

                additionalPages = checked(additionalPages + 1);

                _ = RecordCodec.Encode(
                    mutation.Key,
                    mutation.Value.Span,
                    usesOverflow ? new PageId(1) : PageId.Invalid,
                    tombstone: false,
                    _options);
            }

            try
            {
                _ = checked(_data.Length + checked(additionalPages * (long)_options.PageSize));
            }
            catch (OverflowException)
            {
                throw new StorageLimitException(
                    "The transaction would exceed the supported persistent file-length range.");
            }
        }
    }

    public void ApplyBatch(IReadOnlyList<StorageMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (mutations.Count == 0)
            {
                return;
            }

            var staged = new List<(StorageMutation Mutation, StoredRecord? Record)>(mutations.Count);
            foreach (var mutation in mutations)
            {
                ArgumentNullException.ThrowIfNull(mutation);
                ValidateKey(mutation.Key);
                if (mutation.Value.Length > _options.MaxValueSize)
                {
                    throw new StorageLimitException("Value exceeds the configured maximum size.");
                }

                if (mutation.IsDelete)
                {
                    if (!_records.ContainsKey(mutation.Key))
                    {
                        continue;
                    }

                    var payload = RecordCodec.Encode(
                        mutation.Key,
                        ReadOnlySpan<byte>.Empty,
                        PageId.Invalid,
                        tombstone: true,
                        _options);
                    AppendPage(PageType.Record, payload);
                    staged.Add((mutation, null));
                    continue;
                }

                if (MutationMatchesCurrent(mutation))
                {
                    continue;
                }

                var overflowHead = mutation.Value.Length > _options.InlineValueCapacity(mutation.Key.Length)
                    ? AppendOverflow(mutation.Value.Span)
                    : PageId.Invalid;
                var recordPayload = RecordCodec.Encode(
                    mutation.Key,
                    mutation.Value.Span,
                    overflowHead,
                    tombstone: false,
                    _options);
                var pageId = AppendPage(PageType.Record, recordPayload);
                var inlineValue = overflowHead.IsValid ? [] : mutation.Value.ToArray();
                staged.Add((mutation, new StoredRecord(pageId, mutation.Value.Length, overflowHead, inlineValue)));
            }

            FlushIfConfigured();
            foreach (var (mutation, record) in staged)
            {
                if (mutation.IsDelete)
                {
                    _records.Remove(mutation.Key);
                }
                else
                {
                    _records[mutation.Key] = record!;
                }
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _data.Flush(flushToDisk: true);
            _metadata.Flush(flushToDisk: true);
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
                _data.Flush(flushToDisk: true);
                _metadata.Flush(flushToDisk: true);
            }
            finally
            {
                _data.Dispose();
                _metadata.Dispose();
                _disposed = true;
            }
        }
    }

    private static FileStream OpenExclusive(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
        }
        catch (IOException exception)
        {
            throw new StorageException($"Could not acquire exclusive storage ownership for '{path}'.", exception);
        }
    }

    private static DatabaseHeader OpenOrCreateHeader(
        FileStream metadata,
        FileStream data,
        StorageOptions options,
        bool allowIncompleteFinalPage)
    {
        if (metadata.Length == 0)
        {
            if (data.Length != 0)
            {
                throw new StorageCorruptionException("Data pages exist without a database header.");
            }

            var header = new DatabaseHeader(
                Guid.NewGuid(),
                options.PageSize,
                FormatFlags: 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteExactly(metadata, DatabaseHeaderCodec.Encode(header), 0);
            metadata.Flush(flushToDisk: true);
            return header;
        }

        if (metadata.Length != DatabaseHeaderCodec.Size)
        {
            throw new StorageCorruptionException("Database header file is truncated or has trailing bytes.");
        }

        var bytes = new byte[DatabaseHeaderCodec.Size];
        ReadExactly(metadata, bytes, 0);
        var existingHeader = DatabaseHeaderCodec.Decode(bytes);
        if (existingHeader.PageSize != options.PageSize)
        {
            throw new StorageFormatException("Database page size does not match the requested storage options.");
        }

        if (!allowIncompleteFinalPage && data.Length % options.PageSize != 0)
        {
            throw new StorageCorruptionException("Data file length is not page aligned.");
        }

        return existingHeader;
    }

    private void ScanDataFile()
    {
        var trustedLength = _untrustedTailOffset ?? _data.Length;
        if (trustedLength % _options.PageSize != 0)
        {
            throw new StorageCorruptionException("Trusted data length is not page aligned.");
        }

        var pageCount = checked((ulong)(trustedLength / _options.PageSize));
        var page = new byte[_options.PageSize];

        for (ulong value = 1; value <= pageCount; value++)
        {
            var pageId = new PageId(value);
            try
            {
                ReadExactly(_data, page, PageOffset(pageId));
                var decoded = PageCodec.Decode(page, _options.PageSize);
                if (decoded.Header.PageId != pageId)
                {
                    throw new StorageCorruptionException("Data file page IDs are not sequential.");
                }

                switch (decoded.Header.Type)
                {
                    case PageType.Record:
                        {
                            var record = RecordCodec.Decode(decoded.Payload, _options);
                            if (record.IsTombstone)
                            {
                                _records.Remove(record.Key);
                            }
                            else
                            {
                                _records[record.Key] = new StoredRecord(
                                    pageId,
                                    record.ValueLength,
                                    record.OverflowHead,
                                    record.InlineValue);
                            }

                            break;
                        }
                    case PageType.Overflow:
                        {
                            var overflow = OverflowCodec.Decode(decoded.Payload, _options.PageSize);
                            if (overflow.NextPage.IsValid && overflow.NextPage.Value > pageCount)
                            {
                                throw new StorageCorruptionException(
                                    "Overflow chain points outside the data file.");
                            }

                            ValidateNextOverflowPage(pageId, overflow.NextPage, pageCount);
                            break;
                        }
                    default:
                        throw new StorageCorruptionException("Data file contains an unknown page type.");
                }
            }
            catch (StorageCorruptionException) when (_allowIncompleteFinalPage)
            {
                // In recovery mode, stop at the first corrupt append page without mutating
                // the file. WAL recovery decides whether this offset belongs to the latest
                // durable transaction and is therefore safe to rebuild.
                MarkUntrustedTail(
                    PageOffset(pageId),
                    isFinalAppend: pageId.Value == pageCount,
                    isPartialPage: false);
                break;
            }
        }

        foreach (var record in _records.Values)
        {
            if (record.OverflowHead.IsValid)
            {
                _ = ReadOverflow(record.OverflowHead, record.ValueLength);
            }
        }
    }

    private void MarkUntrustedTail(long offset, bool isFinalAppend, bool isPartialPage)
    {
        if (_untrustedTailOffset is null || offset < _untrustedTailOffset.Value)
        {
            _untrustedTailOffset = offset;
            _untrustedTailIsFinalAppend = isFinalAppend;
            _untrustedTailIsPartialPage = isPartialPage;
            return;
        }

        if (offset == _untrustedTailOffset.Value)
        {
            _untrustedTailIsFinalAppend &= isFinalAppend;
            _untrustedTailIsPartialPage &= isPartialPage;
        }
    }

    private PageId AppendOverflow(ReadOnlySpan<byte> value)
    {
        var chunkCapacity = checked(_options.PageSize - PageCodec.Size - OverflowCodec.HeaderSize);
        var pageCount = checked((value.Length + chunkCapacity - 1) / chunkCapacity);
        var firstPage = new PageId(GetPageCount() + 1);

        for (var index = 0; index < pageCount; index++)
        {
            var offset = checked(index * chunkCapacity);
            var length = Math.Min(chunkCapacity, value.Length - offset);
            var currentPage = new PageId(firstPage.Value + checked((ulong)index));
            var nextPage = index == pageCount - 1
                ? PageId.Invalid
                : new PageId(currentPage.Value + 1);
            var payload = OverflowCodec.Encode(nextPage, value.Slice(offset, length), _options.PageSize);
            AppendPage(PageType.Overflow, payload, currentPage);
        }

        return firstPage;
    }

    private PageId AppendPage(
        PageType type,
        byte[] payload,
        PageId? expectedPage = null)
    {
        var pageId = new PageId(GetPageCount() + 1);
        if (expectedPage is { } expected && expected != pageId)
        {
            throw new StorageException("Append page allocation became inconsistent.");
        }

        var page = PageCodec.Encode(
            new PageHeader(pageId, type, Generation: 1, checked((ushort)payload.Length)),
            payload,
            _options.PageSize);
        _options.FaultInjector?.Hit(StorageFaultPoint.BeforePageWrite, pageId);
        WriteExactly(_data, page, PageOffset(pageId));
        _options.FaultInjector?.Hit(StorageFaultPoint.AfterPageWrite, pageId);
        return pageId;
    }

    private byte[] ReadOverflow(PageId head, int expectedLength)
    {
        var result = new byte[expectedLength];
        var visited = new HashSet<PageId>();
        var current = head;
        var offset = 0;

        while (current.IsValid)
        {
            if (!visited.Add(current) || current.Value > GetPageCount())
            {
                throw new StorageCorruptionException("Overflow chain is cyclic or points outside the data file.");
            }

            var decoded = ReadPage(current);
            if (decoded.Header.Type != PageType.Overflow)
            {
                throw new StorageCorruptionException("Record points to a non-overflow page.");
            }

            var overflow = OverflowCodec.Decode(decoded.Payload, _options.PageSize);
            if (overflow.Chunk.Length > result.Length - offset)
            {
                throw new StorageCorruptionException("Overflow chain contains more bytes than declared.");
            }

            overflow.Chunk.CopyTo(result.AsSpan(offset));
            offset += overflow.Chunk.Length;
            ValidateNextOverflowPage(decoded.Header.PageId, overflow.NextPage, GetPageCount());
            current = overflow.NextPage;
        }

        if (offset != result.Length)
        {
            throw new StorageCorruptionException("Overflow chain contains fewer bytes than declared.");
        }

        return result;
    }

    private DecodedPage ReadPage(PageId pageId)
    {
        var bytes = new byte[_options.PageSize];
        ReadExactly(_data, bytes, PageOffset(pageId));
        var decoded = PageCodec.Decode(bytes, _options.PageSize);
        if (decoded.Header.PageId != pageId)
        {
            throw new StorageCorruptionException("Referenced page ID does not match its physical page.");
        }

        return decoded;
    }

    private ulong GetPageCount()
    {
        var length = _untrustedTailOffset ?? _data.Length;
        if (length % _options.PageSize != 0)
        {
            throw new StorageCorruptionException("Data file length is not page aligned.");
        }

        return checked((ulong)(length / _options.PageSize));
    }

    private long PageOffset(PageId pageId)
    {
        if (!pageId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        return checked((long)(pageId.Value - 1) * _options.PageSize);
    }

    private void FlushIfConfigured()
    {
        if (_options.FlushOnWrite)
        {
            _data.Flush(flushToDisk: true);
        }
    }

    private void ValidateKey(BinaryKey key)
    {
        if (key.Length > _options.MaxKeySize)
        {
            throw new StorageLimitException("Key exceeds the configured maximum size.");
        }
    }

    private bool MutationMatchesCurrent(StorageMutation mutation)
    {
        if (!_records.TryGetValue(mutation.Key, out var current) || current.ValueLength != mutation.Value.Length)
        {
            return false;
        }

        var currentValue = current.InlineValue.Length != 0 || current.ValueLength == 0
            ? current.InlineValue
            : ReadOverflow(current.OverflowHead, current.ValueLength);
        return currentValue.AsSpan().SequenceEqual(mutation.Value.Span);
    }

    private static void ValidateNextOverflowPage(PageId current, PageId next, ulong pageCount)
    {
        if (next.IsValid && (next.Value <= current.Value || next.Value > pageCount))
        {
            throw new StorageCorruptionException("Overflow chain must point to a later page in the data file.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                throw new StorageCorruptionException("Persistent file ended before the requested bytes were read.");
            }

            read += count;
        }
    }

    private static void WriteExactly(Stream stream, byte[] buffer, long offset)
    {
        stream.Position = offset;
        stream.Write(buffer, 0, buffer.Length);
    }

    private sealed record StoredRecord(
        PageId RecordPage,
        int ValueLength,
        PageId OverflowHead,
        byte[] InlineValue);
}
