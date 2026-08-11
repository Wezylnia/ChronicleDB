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
    private FileStream _data;
    private readonly bool _allowIncompleteFinalPage;
    private readonly Guid _databaseId;
    private readonly Dictionary<BinaryKey, StoredRecord> _records = [];
    private DatabaseHeader _header;
    private long? _untrustedTailOffset;
    private bool _untrustedTailIsFinalAppend;
    private bool _untrustedTailIsPartialPage;
    private long _overflowPageCount;
    private bool _faulted;
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
        _databaseId = header.DatabaseId;
        _header = header;
    }

    public DatabaseHeader Header
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _header;
            }
        }
    }

    public Guid DatabaseId => _databaseId;

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

    public bool HasFormatFlag(uint formatFlag)
    {
        if ((formatFlag & ~DatabaseHeader.SupportedFormatFlags) != 0 || formatFlag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatFlag));
        }

        lock (_gate)
        {
            ThrowIfUsable();
            return (_header.FormatFlags & formatFlag) == formatFlag;
        }
    }

    public void EnsureFormatFlags(uint requiredFlags)
    {
        if (requiredFlags == 0 || (requiredFlags & ~DatabaseHeader.SupportedFormatFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFlags));
        }

        lock (_gate)
        {
            ThrowIfUsable();
            if ((_header.FormatFlags & requiredFlags) == requiredFlags)
            {
                return;
            }

            if (_header.Generation == ulong.MaxValue)
            {
                throw new StorageLimitException("Database metadata generation space is exhausted.");
            }

            var nextGeneration = _header.Generation == 0 ? 1UL : _header.Generation + 1;
            var nextHeader = _header with
            {
                FormatFlags = _header.FormatFlags | requiredFlags,
                Generation = nextGeneration
            };
            var encoded = DatabaseHeaderCodec.Encode(nextHeader);
            var startingLength = _metadata.Length;
            try
            {
                _metadata.Position = startingLength;
                _metadata.Write(encoded);
                _metadata.Flush(flushToDisk: true);
                _header = nextHeader;
            }
            catch (Exception exception)
            {
                if (exception is IOException or UnauthorizedAccessException || _metadata.Length != startingLength)
                {
                    _faulted = true;
                }

                throw;
            }
        }
    }

    internal bool HasUntrustedTail
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
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
                ThrowIfUsable();
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
                ThrowIfUsable();
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
                ThrowIfUsable();
                return _untrustedTailIsPartialPage;
            }
        }
    }

    public long DataLength
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _data.Length;
            }
        }
    }

    internal bool CanRepairFrom(long baseDataLength)
    {
        lock (_gate)
        {
            ThrowIfUsable();
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
            ThrowIfUsable();
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
            ThrowIfUsable();
            if (!CanRepairFrom(safeBaseDataLength))
            {
                throw new StorageCorruptionException(
                    "The WAL recovery base is not a valid prefix of the untrusted data tail.");
            }

            DiscardUntrustedTailCore(safeBaseDataLength);
        }
    }

    /// <summary>
    /// Truncates an append-only auxiliary store to a previously durably published page boundary.
    /// This is intentionally internal and must only be used when a separate authoritative
    /// metadata protocol proves that every byte after <paramref name="safeDataLength"/> is orphaned.
    /// </summary>
    internal void RecoverAppendOnlyPrefix(long safeDataLength)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            if (safeDataLength < 0
                || safeDataLength % _options.PageSize != 0
                || safeDataLength > _data.Length)
            {
                throw new StorageCorruptionException(
                    "The published append-only recovery boundary is not a valid data-file prefix.");
            }

            if (_untrustedTailOffset is { } tailOffset && safeDataLength > tailOffset)
            {
                throw new StorageCorruptionException(
                    "Corruption begins inside the durably published append-only prefix.");
            }

            if (safeDataLength == _data.Length && !_untrustedTailOffset.HasValue)
            {
                return;
            }

            _data.SetLength(safeDataLength);
            _data.Flush(flushToDisk: true);
            _untrustedTailOffset = null;
            _untrustedTailIsFinalAppend = false;
            _untrustedTailIsPartialPage = false;
            _records.Clear();
            ScanDataFile();
            if (_untrustedTailOffset.HasValue)
            {
                throw new StorageCorruptionException(
                    "The durably published append-only prefix remains corrupt after tail recovery.");
            }
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
                ThrowIfUsable();
                return _records.Count;
            }
        }
    }

    public long PageCount
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return checked((_untrustedTailOffset ?? _data.Length) / _options.PageSize);
            }
        }
    }

    public long OverflowPageCount
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _overflowPageCount;
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

        var metadataPath = Path.Combine(fullDirectory, MetadataFileName);
        var dataPath = Path.Combine(fullDirectory, DataFileName);
        var backupPath = dataPath + ".previous";
        RecoverInterruptedCompaction(dataPath);

        // If copy-and-publish compaction crashed after the replacement became visible
        // but before retiring .previous, validate the replacement before discarding
        // the known-good prior generation. A corrupt or torn replacement falls back
        // exactly once to the previous physical representation.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            FileStream? metadata = null;
            FileStream? data = null;
            PersistentKeyValueStore? store = null;
            try
            {
                EnsureMetadataCreatedAtomically(metadataPath, dataPath, validatedOptions);
                metadata = OpenExclusive(metadataPath);
                data = OpenExclusive(dataPath);
                var header = OpenOrCreateHeader(metadata, data, validatedOptions, allowIncompleteFinalPage);
                store = new PersistentKeyValueStore(
                    validatedOptions,
                    metadata,
                    data,
                    header,
                    allowIncompleteFinalPage);
                metadata = null;
                data = null;

                if (store._data.Length % validatedOptions.PageSize != 0)
                {
                    store._untrustedTailOffset = store._data.Length - (store._data.Length % validatedOptions.PageSize);
                    store._untrustedTailIsFinalAppend = true;
                    store._untrustedTailIsPartialPage = true;
                }

                store.ScanDataFile();
                if (File.Exists(backupPath) && store._untrustedTailOffset.HasValue)
                {
                    store.Dispose();
                    store = null;
                    RestoreCompactionBackup(dataPath, backupPath);
                    continue;
                }

                TryDeleteCompactionBackup(backupPath);
                return store;
            }
            catch (StorageCorruptionException) when (attempt == 0 && File.Exists(backupPath))
            {
                store?.Dispose();
                data?.Dispose();
                metadata?.Dispose();
                store = null;
                data = null;
                metadata = null;
                RestoreCompactionBackup(dataPath, backupPath);
            }
            catch
            {
                store?.Dispose();
                data?.Dispose();
                metadata?.Dispose();
                throw;
            }
        }

        throw new StorageCorruptionException(
            "Neither the published compacted data nor its previous generation could be opened safely.");
    }

    internal IReadOnlyList<StorageMutation> SnapshotCurrentState()
    {
        lock (_gate)
        {
            ThrowIfUsable();
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
            ThrowIfUsable();
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
            ThrowIfUsable();
            ValidateKey(key);
            if (value.Length > _options.MaxValueSize)
            {
                throw new StorageLimitException("Value exceeds the configured maximum size.");
            }

            var startingLength = _data.Length;
            try
            {
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
            catch (Exception exception)
            {
                MarkFaultedIfWriteOutcomeIsUncertain(startingLength, exception);
                throw;
            }
        }
    }

    public bool Delete(BinaryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            ThrowIfUsable();
            ValidateKey(key);
            var existed = _records.ContainsKey(key);
            var startingLength = _data.Length;
            try
            {
                var payload = RecordCodec.Encode(key, ReadOnlySpan<byte>.Empty, PageId.Invalid, tombstone: true, _options);
                AppendPage(PageType.Record, payload);
                FlushIfConfigured();
                _records.Remove(key);
                return existed;
            }
            catch (Exception exception)
            {
                MarkFaultedIfWriteOutcomeIsUncertain(startingLength, exception);
                throw;
            }
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
            ThrowIfUsable();
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
            ThrowIfUsable();
            if (mutations.Count == 0)
            {
                return;
            }

            var startingLength = _data.Length;
            try
            {
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
            catch (Exception exception)
            {
                MarkFaultedIfWriteOutcomeIsUncertain(startingLength, exception);
                throw;
            }
        }
    }

    /// <summary>
    /// Computes the exact data-file length produced by <see cref="RewriteState"/> for
    /// the supplied surviving logical state under this store's page/overflow layout.
    /// This is used by v0.9 compaction planning so an already compacted file is not
    /// repeatedly rewritten because of a byte-level heuristic.
    /// </summary>
    public long EstimateRewriteDataLength(IReadOnlyList<StorageMutation> desiredState)
    {
        ArgumentNullException.ThrowIfNull(desiredState);
        lock (_gate)
        {
            ThrowIfUsable();
            var normalized = NormalizeRewriteState(desiredState);
            long pageCount = 0;
            var overflowChunkCapacity = checked(_options.PageSize - PageHeader.Size - OverflowCodec.HeaderSize);
            foreach (var mutation in normalized)
            {
                pageCount = checked(pageCount + 1); // one record page
                if (mutation.Value.Length > _options.InlineValueCapacity(mutation.Key.Length))
                {
                    var overflowPages = checked(
                        (mutation.Value.Length + overflowChunkCapacity - 1L) / overflowChunkCapacity);
                    pageCount = checked(pageCount + overflowPages);
                }
            }

            return checked(pageCount * _options.PageSize);
        }
    }

    /// <summary>
    /// Rewrites the complete logical current map into a fresh data file and publishes
    /// it with a recoverable two-rename protocol. Callers must already have established
    /// that the supplied state is the complete physical representation they intend to keep.
    /// </summary>
    public StorageRewriteResult RewriteState(IReadOnlyList<StorageMutation> desiredState)
        => RewriteStateCore(desiredState, injectPublicationFaults: true);

    /// <summary>
    /// Rebuilds a derived physical representation from an independently authoritative
    /// recovery source. Recovery must not accidentally trigger foreground compaction
    /// fault points, so publication injection is disabled on this path.
    /// </summary>
    internal StorageRewriteResult RewriteStateForRecovery(IReadOnlyList<StorageMutation> desiredState)
        => RewriteStateCore(desiredState, injectPublicationFaults: false);

    private StorageRewriteResult RewriteStateCore(
        IReadOnlyList<StorageMutation> desiredState,
        bool injectPublicationFaults)
    {
        ArgumentNullException.ThrowIfNull(desiredState);
        lock (_gate)
        {
            ThrowIfUsable();
            var state = NormalizeRewriteState(desiredState);
            var oldLength = _data.Length;
            var dataPath = _data.Name;
            var directory = Path.GetDirectoryName(dataPath)
                ?? throw new StorageException("Persistent data file has no parent directory.");
            var backupPath = dataPath + ".previous";
            var dataClosed = false;
            var publicationTouched = false;
            var tempDirectory = Path.Combine(directory, ".compact-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var tempMetadata = Path.Combine(tempDirectory, MetadataFileName);
            var tempData = Path.Combine(tempDirectory, DataFileName);

            try
            {
                File.WriteAllBytes(tempMetadata, DatabaseHeaderCodec.Encode(_header));
                var tempOptions = _options with { FlushOnWrite = false };
                using (var temporary = Open(tempDirectory, tempOptions))
                {
                    temporary.ValidateBatch(state);
                    temporary.ApplyBatch(state);
                    temporary.Flush();
                    var actual = temporary.SnapshotCurrentState();
                    if (!StateEquals(state, actual))
                    {
                        throw new StorageCorruptionException("Compaction output failed logical self-validation.");
                    }
                }

                if (injectPublicationFaults)
                {
                    _options.FaultInjector?.Hit(StorageFaultPoint.BeforeCompactionPublish, PageId.Invalid);
                }
                // From this point onward the authoritative file publication protocol has
                // begun. Any exception requires reopen before this store instance may be
                // trusted again, even when recovery can deterministically choose a file.
                publicationTouched = true;
                _data.Flush(flushToDisk: true);
                _data.Dispose();
                dataClosed = true;
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(dataPath, backupPath, overwrite: true);
                try
                {
                    File.Move(tempData, dataPath, overwrite: true);
                }
                catch
                {
                    if (!File.Exists(dataPath) && File.Exists(backupPath))
                    {
                        File.Move(backupPath, dataPath, overwrite: true);
                    }
                    throw;
                }

                _data = OpenExclusive(dataPath);
                dataClosed = false;
                _untrustedTailOffset = null;
                _untrustedTailIsFinalAppend = false;
                _untrustedTailIsPartialPage = false;
                ScanDataFile();
                if (!StateEquals(state, SnapshotCurrentStateLocked()))
                {
                    _faulted = true;
                    throw new StorageCorruptionException("Published compacted data does not match the requested state.");
                }
                if (injectPublicationFaults)
                {
                    _options.FaultInjector?.Hit(StorageFaultPoint.AfterCompactionPublish, PageId.Invalid);
                    _options.FaultInjector?.Hit(StorageFaultPoint.BeforeCompactionCleanup, PageId.Invalid);
                }
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                if (injectPublicationFaults)
                {
                    _options.FaultInjector?.Hit(StorageFaultPoint.AfterCompactionCleanup, PageId.Invalid);
                }
                return new StorageRewriteResult(oldLength, _data.Length);
            }
            catch
            {
                if (dataClosed)
                {
                    if (!File.Exists(dataPath) && File.Exists(backupPath))
                    {
                        File.Move(backupPath, dataPath, overwrite: true);
                    }
                    if (File.Exists(dataPath))
                    {
                        _data = OpenExclusive(dataPath);
                        dataClosed = false;
                        _untrustedTailOffset = null;
                        _untrustedTailIsFinalAppend = false;
                        _untrustedTailIsPartialPage = false;
                        ScanDataFile();
                    }
                }
                if (publicationTouched)
                {
                    _faulted = true;
                }
                throw;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Orphaned temporary output is never authoritative.
                }
            }
        }
    }

    private StorageMutation[] NormalizeRewriteState(IReadOnlyList<StorageMutation> desiredState)
    {
        var normalized = new Dictionary<BinaryKey, StorageMutation>();
        foreach (var mutation in desiredState)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            if (mutation.IsDelete)
            {
                throw new ArgumentException(
                    "Compacted state must contain only surviving put records.",
                    nameof(desiredState));
            }

            ValidateKey(mutation.Key);
            if (mutation.Value.Length > _options.MaxValueSize)
            {
                throw new StorageLimitException("Compacted value exceeds the configured maximum size.");
            }
            normalized[mutation.Key] = mutation;
        }
        return normalized.Values.ToArray();
    }

    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            try
            {
                _data.Flush(flushToDisk: true);
                _metadata.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _faulted = true;
                throw new StorageException("Persistent storage flush failed and the store must be reopened.", exception);
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
                    _data.Flush(flushToDisk: true);
                    _metadata.Flush(flushToDisk: true);
                }
            }
            finally
            {
                _data.Dispose();
                _metadata.Dispose();
                _disposed = true;
            }
        }
    }

    private List<StorageMutation> SnapshotCurrentStateLocked()
    {
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

    private static bool StateEquals(
        StorageMutation[] expected,
        IReadOnlyList<StorageMutation> actual)
    {
        if (expected.Length != actual.Count)
        {
            return false;
        }
        var map = expected.ToDictionary(item => item.Key, item => item.Value.ToArray());
        foreach (var item in actual)
        {
            if (!map.TryGetValue(item.Key, out var value) || !value.AsSpan().SequenceEqual(item.Value.Span))
            {
                return false;
            }
        }
        return true;
    }

    private static void RecoverInterruptedCompaction(string dataPath)
    {
        var backupPath = dataPath + ".previous";
        if (File.Exists(backupPath) && !File.Exists(dataPath))
        {
            File.Move(backupPath, dataPath, overwrite: true);
        }
        // When both generations exist, Open validates the new primary before
        // retiring the previous generation. Do not delete recovery evidence here.
    }

    private static void RestoreCompactionBackup(string dataPath, string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new StorageCorruptionException(
                "Compaction recovery requires a previous data generation, but it is missing.");
        }

        File.Move(backupPath, dataPath, overwrite: true);
    }

    private static void TryDeleteCompactionBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch (IOException)
        {
            // A validated primary remains authoritative. Leaving the backup is safe
            // and the next open/compaction pass may retire it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: cleanup failure must not invalidate a verified primary.
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


    private static void EnsureMetadataCreatedAtomically(
        string metadataPath,
        string dataPath,
        StorageOptions options)
    {
        if (File.Exists(metadataPath))
        {
            return;
        }

        if (File.Exists(dataPath) && new FileInfo(dataPath).Length != 0)
        {
            throw new StorageCorruptionException("Data pages exist without database metadata.");
        }

        var temporaryPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".creating";
        try
        {
            var header = new DatabaseHeader(
                Guid.NewGuid(),
                options.PageSize,
                FormatFlags: 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Generation: 1);
            using (var temporary = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4 * 1024,
                       options: FileOptions.WriteThrough))
            {
                temporary.Write(DatabaseHeaderCodec.Encode(header));
                temporary.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, metadataPath);
            }
            catch (IOException) when (File.Exists(metadataPath))
            {
                // Another opener won atomic creation. Exclusive open and header validation
                // below decide whether this process can continue.
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

    private static DatabaseHeader OpenOrCreateHeader(
        FileStream metadata,
        FileStream data,
        StorageOptions options,
        bool allowIncompleteFinalPage)
    {
        if (metadata.Length == 0)
        {
            throw new StorageCorruptionException(
                "Database metadata exists but contains no durable header generation.");
        }

        if (metadata.Length < DatabaseHeaderCodec.Size)
        {
            throw new StorageCorruptionException("Database header file is truncated.");
        }

        var completeLength = metadata.Length - metadata.Length % DatabaseHeaderCodec.Size;
        var slotCount = checked((int)(completeLength / DatabaseHeaderCodec.Size));
        var bytes = new byte[DatabaseHeaderCodec.Size];
        DatabaseHeader? latest = null;
        for (var slot = 0; slot < slotCount; slot++)
        {
            ReadExactly(metadata, bytes, checked((long)slot * DatabaseHeaderCodec.Size));
            var candidate = DatabaseHeaderCodec.Decode(bytes);
            if (latest is not null)
            {
                ValidateHeaderSuccessor(latest, candidate);
            }

            latest = candidate;
        }

        if (metadata.Length != completeLength)
        {
            // Metadata updates are append-only. A short final slot can only be an
            // incomplete later generation; the previous checksummed slot remains
            // authoritative. Never repair a damaged complete slot this way.
            metadata.SetLength(completeLength);
            metadata.Flush(flushToDisk: true);
        }

        var existingHeader = latest
            ?? throw new StorageCorruptionException("Database metadata contains no complete header slot.");
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

    private static void ValidateHeaderSuccessor(DatabaseHeader previous, DatabaseHeader next)
    {
        if (next.DatabaseId != previous.DatabaseId
            || next.PageSize != previous.PageSize
            || next.CreatedUnixMilliseconds != previous.CreatedUnixMilliseconds)
        {
            throw new StorageCorruptionException("Database metadata generations disagree on immutable identity fields.");
        }

        if (next.Generation <= previous.Generation)
        {
            throw new StorageCorruptionException("Database metadata generations are not strictly increasing.");
        }

        if ((next.FormatFlags & previous.FormatFlags) != previous.FormatFlags)
        {
            throw new StorageCorruptionException("Database metadata removed a previously durable format capability flag.");
        }
    }

    private void ScanDataFile()
    {
        _records.Clear();
        _overflowPageCount = 0;
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
                            _overflowPageCount++;
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
        if (type == PageType.Overflow)
        {
            _overflowPageCount++;
        }

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

    private void ThrowIfUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new StorageException(
                "Persistent storage is faulted after an uncertain I/O operation and must be reopened.");
        }
    }

    private void MarkFaultedIfWriteOutcomeIsUncertain(long startingLength, Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException || _data.Length != startingLength)
        {
            _faulted = true;
        }
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

public readonly record struct StorageRewriteResult(long OldBytes, long NewBytes)
{
    public long ReclaimedBytes => Math.Max(0, OldBytes - NewBytes);
}
