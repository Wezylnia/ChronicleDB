using System.Buffers.Binary;
using System.Text;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Faults;

namespace ChronicleDB.Storage.Branches;

/// <summary>
/// Database-bound append-only branch metadata journal used by v0.7.
/// It persists branch identity/ancestry, creation intent, activation and the
/// authoritative local commit sequence. Branch transaction WAL is deliberately
/// deferred to v0.8; AdvanceSequence records are not a substitute for WAL.
/// </summary>
public sealed class PersistentBranchMetadataStore : IDisposable
{
    public const string FileName = "chronicle.branches";

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly IStorageFaultInjector? _faultInjector;
    private readonly Dictionary<BranchId, BranchStoreRecord> _states = [];
    private readonly Dictionary<BranchId, List<BranchCommitDescriptor>> _commits = [];
    private readonly HashSet<(BranchId BranchId, TransactionId TransactionId)> _seenTransactions = [];
    private readonly HashSet<BranchId> _seenBranchIds = [];
    private readonly HashSet<HistoryId> _seenHistoryIds = [];
    private readonly Dictionary<string, BranchId> _reservedNames = new(StringComparer.Ordinal);
    private ulong _nextEventSequence;
    private bool _faulted;
    private bool _disposed;

    private PersistentBranchMetadataStore(
        FileStream stream,
        BranchStoreHeader header,
        IStorageFaultInjector? faultInjector)
    {
        _stream = stream;
        Header = header;
        _faultInjector = faultInjector;
    }

    public BranchStoreHeader Header { get; }

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

    public static PersistentBranchMetadataStore Open(
        string directory,
        Guid databaseId,
        HistoryId mainHistoryId,
        IStorageFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (databaseId == Guid.Empty || !mainHistoryId.IsValid)
        {
            throw new ArgumentException("A branch metadata store requires valid database and Main-history identities.");
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, FileName);
        FileStream? stream = null;
        try
        {
            EnsureCreatedAtomically(path, new BranchStoreHeader(databaseId, mainHistoryId));
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
                throw new StorageFormatException("Branch metadata identity does not match the database.");
            }

            var store = new PersistentBranchMetadataStore(stream, header, faultInjector);
            store.ScanAndRecoverTail();
            return store;
        }
        catch (IOException exception)
        {
            stream?.Dispose();
            throw new StorageException($"Could not open branch metadata '{path}'.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<BranchStoreRecord> ListActive()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _states.Values
                .Where(IsActiveState)
                .OrderBy(record => record.Depth)
                .ThenBy(record => record.Name, StringComparer.Ordinal)
                .ThenBy(record => record.BranchId.Value)
                .ToArray();
        }
    }

    public IReadOnlyList<BranchStoreRecord> ListCreating()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _states.Values
                .Where(record => record.Type == BranchStoreRecordType.CreateIntent)
                .OrderBy(record => record.EventSequence)
                .ToArray();
        }
    }

    public IReadOnlyList<BranchCommitDescriptor> ListCommits(BranchId branchId)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _commits.TryGetValue(branchId, out var commits)
                ? commits.ToArray()
                : [];
        }
    }

    public bool TryGet(BranchId branchId, out BranchStoreRecord? state)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return _states.TryGetValue(branchId, out state);
        }
    }

    public void EnsureNameAvailable(string name)
    {
        var normalized = ValidateName(name);
        lock (_gate)
        {
            ThrowIfUsable();
            if (_reservedNames.ContainsKey(normalized))
            {
                throw new StorageException($"Branch name '{normalized}' is already reserved.");
            }
        }
    }

    public BranchStoreRecord AppendCreateIntent(
        BranchId branchId,
        HistoryId historyId,
        HistoryId parentHistoryId,
        HistoryRootId baseRootId,
        CommitSequence parentBaseSequence,
        long createdUnixMilliseconds,
        int depth,
        string name)
    {
        var normalized = ValidateName(name);
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (historyId == Header.MainHistoryId
                || _seenBranchIds.Contains(branchId)
                || _seenHistoryIds.Contains(historyId))
            {
                throw new StorageException("Branch or history identity has already been used.");
            }

            if (_reservedNames.ContainsKey(normalized))
            {
                throw new StorageException($"Branch name '{normalized}' is already reserved.");
            }

            var record = new BranchStoreRecord(
                BranchStoreRecordType.CreateIntent,
                _nextEventSequence,
                branchId,
                historyId,
                parentHistoryId,
                baseRootId,
                parentBaseSequence,
                CommitSequence.Initial,
                Guid.Empty,
                TransactionId.Empty,
                0,
                0,
                createdUnixMilliseconds,
                depth,
                normalized);
            AppendAndApply(record);
            return record;
        }
    }

    public BranchStoreRecord AppendActivate(BranchId branchId, Guid localStorageId)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (localStorageId == Guid.Empty
                || !_states.TryGetValue(branchId, out var intent)
                || intent.Type != BranchStoreRecordType.CreateIntent)
            {
                throw new StorageException("Only a valid branch creation intent may be activated.");
            }

            var record = intent with
            {
                Type = BranchStoreRecordType.Activate,
                EventSequence = _nextEventSequence,
                LocalStorageId = localStorageId,
            };
            AppendAndApply(record);
            return record;
        }
    }

    public BranchStoreRecord AppendAbandonCreate(BranchId branchId)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            if (!_states.TryGetValue(branchId, out var intent)
                || intent.Type != BranchStoreRecordType.CreateIntent)
            {
                throw new StorageException("Only an incomplete branch creation may be abandoned.");
            }

            var record = intent with
            {
                Type = BranchStoreRecordType.AbandonCreate,
                EventSequence = _nextEventSequence,
            };
            AppendAndApply(record);
            return record;
        }
    }

    public void ValidateAdvance(
        BranchId branchId,
        CommitSequence nextSequence,
        TransactionId transactionId,
        int mutationCount,
        long dataLengthAfterCommit)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            ValidateAdvanceLocked(branchId, nextSequence, transactionId, mutationCount, dataLengthAfterCommit);
            EnsureEventSequenceAvailable();
        }
    }

    public BranchStoreRecord AppendAdvance(
        BranchId branchId,
        CommitSequence nextSequence,
        TransactionId transactionId,
        int mutationCount,
        long dataLengthAfterCommit)
    {
        lock (_gate)
        {
            ThrowIfUsable();
            EnsureEventSequenceAvailable();
            var current = ValidateAdvanceLocked(
                branchId,
                nextSequence,
                transactionId,
                mutationCount,
                dataLengthAfterCommit);
            var record = current with
            {
                Type = BranchStoreRecordType.AdvanceSequence,
                EventSequence = _nextEventSequence,
                LocalCommitSequence = nextSequence,
                TransactionId = transactionId,
                MutationCount = mutationCount,
                DataLengthAfterCommit = dataLengthAfterCommit,
            };
            AppendAndApply(record);
            return record;
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

    private BranchStoreRecord ValidateAdvanceLocked(
        BranchId branchId,
        CommitSequence nextSequence,
        TransactionId transactionId,
        int mutationCount,
        long dataLengthAfterCommit)
    {
        if (!_states.TryGetValue(branchId, out var current) || !IsActiveState(current))
        {
            throw new StorageException($"Branch {branchId.Value} is not active.");
        }

        if (!transactionId.IsValid || mutationCount < 0 || dataLengthAfterCommit < 0)
        {
            throw new StorageFormatException("Branch commit descriptor is invalid.");
        }

        if (_seenTransactions.Contains((branchId, transactionId)))
        {
            throw new StorageException("A branch transaction identity cannot be committed more than once.");
        }

        CommitSequence expected;
        try
        {
            expected = current.LocalCommitSequence.Next();
        }
        catch (OverflowException exception)
        {
            throw new StorageLimitException("Branch commit-sequence space is exhausted.", exception);
        }

        if (nextSequence != expected || dataLengthAfterCommit < current.DataLengthAfterCommit)
        {
            throw new StorageException("Branch commit descriptor does not extend the active branch history.");
        }

        return current;
    }

    private void AppendAndApply(BranchStoreRecord record)
    {
        var encoded = BranchStoreRecordCodec.Encode(record);
        var persistenceTouched = false;
        try
        {
            _faultInjector?.Hit(StorageFaultPoint.BeforeBranchMetadataRecordWrite, PageId.Invalid);
            _stream.Position = _stream.Length;
            persistenceTouched = true;
            _stream.Write(encoded);
            _faultInjector?.Hit(StorageFaultPoint.AfterBranchMetadataRecordWrite, PageId.Invalid);
            _faultInjector?.Hit(StorageFaultPoint.BeforeBranchMetadataFlush, PageId.Invalid);
            _stream.Flush(flushToDisk: true);
            _faultInjector?.Hit(StorageFaultPoint.AfterBranchMetadataFlush, PageId.Invalid);
        }
        catch
        {
            if (persistenceTouched)
            {
                _faulted = true;
            }

            throw;
        }

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

    private void ScanAndRecoverTail()
    {
        var position = (long)BranchStoreHeaderCodec.Size;
        var expectedEventSequence = 1UL;
        while (position < _stream.Length)
        {
            var remaining = _stream.Length - position;
            if (remaining < BranchStoreRecordCodec.HeaderSize)
            {
                TruncateTail(position);
                break;
            }

            var header = new byte[BranchStoreRecordCodec.HeaderSize];
            ReadExactly(_stream, header, position);
            if (!header.AsSpan(0, 4).SequenceEqual("BRN1"u8))
            {
                throw new StorageFormatException("Branch metadata contains a corrupt record magic before the final tail.");
            }

            var repeatedLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
            var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            var maxRecordSize = BranchStoreRecordCodec.HeaderSize + BranchStoreRecordCodec.MaxNameBytes + BranchStoreRecordCodec.FooterSize;
            if (repeatedLength != totalLength
                || totalLength < BranchStoreRecordCodec.HeaderSize + BranchStoreRecordCodec.FooterSize
                || totalLength > maxRecordSize)
            {
                throw new StorageCorruptionException("Branch metadata record length framing is invalid.");
            }

            if (totalLength > remaining)
            {
                TruncateTail(position);
                break;
            }

            var encoded = new byte[checked((int)totalLength)];
            ReadExactly(_stream, encoded, position);
            var record = BranchStoreRecordCodec.Decode(encoded);
            if (record.EventSequence != expectedEventSequence)
            {
                throw new StorageCorruptionException(
                    $"Branch metadata event sequence is discontinuous: expected {expectedEventSequence}, found {record.EventSequence}.");
            }

            ApplyRecoveredRecord(record);
            expectedEventSequence = NextEventSequence(expectedEventSequence);
            position += totalLength;
        }

        _nextEventSequence = expectedEventSequence;
    }

    private void ApplyRecoveredRecord(BranchStoreRecord record)
    {
        switch (record.Type)
        {
            case BranchStoreRecordType.CreateIntent:
                if (record.HistoryId == Header.MainHistoryId
                    || !_seenBranchIds.Add(record.BranchId)
                    || !_seenHistoryIds.Add(record.HistoryId)
                    || _states.ContainsKey(record.BranchId)
                    || _reservedNames.ContainsKey(record.Name))
                {
                    throw new StorageCorruptionException("Branch identity, history identity, or name is reused.");
                }

                _states.Add(record.BranchId, record);
                _reservedNames.Add(record.Name, record.BranchId);
                _commits.Add(record.BranchId, []);
                break;
            case BranchStoreRecordType.Activate:
                EnsureSameCreationMetadata(record, BranchStoreRecordType.CreateIntent);
                if (record.LocalStorageId == Guid.Empty)
                {
                    throw new StorageCorruptionException("Branch activation has no local storage identity.");
                }
                _states[record.BranchId] = record;
                break;
            case BranchStoreRecordType.AdvanceSequence:
                if (!_states.TryGetValue(record.BranchId, out var current) || !IsActiveState(current))
                {
                    throw new StorageCorruptionException("Branch commit metadata references an inactive branch.");
                }

                EnsureImmutableMetadataMatches(current, record);
                CommitSequence expected;
                try
                {
                    expected = current.LocalCommitSequence.Next();
                }
                catch (OverflowException exception)
                {
                    throw new StorageCorruptionException("Branch commit sequence overflows persistent metadata.", exception);
                }

                if (record.LocalCommitSequence != expected
                    || record.LocalStorageId != current.LocalStorageId
                    || record.DataLengthAfterCommit < current.DataLengthAfterCommit)
                {
                    throw new StorageCorruptionException("Branch commit sequence or append boundary is discontinuous.");
                }

                if (!_seenTransactions.Add((record.BranchId, record.TransactionId)))
                {
                    throw new StorageCorruptionException("Branch commit metadata reuses a transaction identity.");
                }

                _states[record.BranchId] = record;
                _commits[record.BranchId].Add(new BranchCommitDescriptor(
                    record.TransactionId,
                    record.LocalCommitSequence,
                    record.MutationCount,
                    record.DataLengthAfterCommit));
                break;
            case BranchStoreRecordType.AbandonCreate:
                EnsureSameCreationMetadata(record, BranchStoreRecordType.CreateIntent);
                _states[record.BranchId] = record;
                _reservedNames.Remove(record.Name);
                break;
            default:
                throw new StorageFormatException("Branch metadata record type is unsupported.");
        }
    }

    private void EnsureSameCreationMetadata(BranchStoreRecord record, BranchStoreRecordType requiredCurrentType)
    {
        if (!_states.TryGetValue(record.BranchId, out var current) || current.Type != requiredCurrentType)
        {
            throw new StorageCorruptionException("Branch lifecycle transition references an invalid state.");
        }

        EnsureImmutableMetadataMatches(current, record);
    }

    private static void EnsureImmutableMetadataMatches(BranchStoreRecord current, BranchStoreRecord next)
    {
        if (current.BranchId != next.BranchId
            || current.HistoryId != next.HistoryId
            || current.ParentHistoryId != next.ParentHistoryId
            || current.BaseRootId != next.BaseRootId
            || current.ParentBaseSequence != next.ParentBaseSequence
            || current.CreatedUnixMilliseconds != next.CreatedUnixMilliseconds
            || current.Depth != next.Depth
            || !string.Equals(current.Name, next.Name, StringComparison.Ordinal))
        {
            throw new StorageCorruptionException("Branch lifecycle metadata changed immutable branch identity fields.");
        }
    }

    private static bool IsActiveState(BranchStoreRecord record)
        => record.Type is BranchStoreRecordType.Activate or BranchStoreRecordType.AdvanceSequence;

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Branch names may not contain leading or trailing whitespace.", nameof(name));
        }

        int byteCount;
        try
        {
            byteCount = new UTF8Encoding(false, true).GetByteCount(name);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Branch names must be valid UTF-8 text.", nameof(name), exception);
        }

        if (byteCount > BranchStoreRecordCodec.MaxNameBytes)
        {
            throw new ArgumentException(
                $"Branch names may use at most {BranchStoreRecordCodec.MaxNameBytes} UTF-8 bytes.",
                nameof(name));
        }

        return name;
    }

    private void TruncateTail(long validLength)
    {
        _stream.SetLength(validLength);
        _stream.Flush(flushToDisk: true);
    }

    private static void EnsureCreatedAtomically(string path, BranchStoreHeader header)
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
                temporary.Write(BranchStoreHeaderCodec.Encode(header));
                temporary.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
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

    private static BranchStoreHeader ReadHeader(FileStream stream)
    {
        if (stream.Length < BranchStoreHeaderCodec.Size)
        {
            throw new StorageCorruptionException("Branch-store header is truncated.");
        }

        var bytes = new byte[BranchStoreHeaderCodec.Size];
        ReadExactly(stream, bytes, 0);
        return BranchStoreHeaderCodec.Decode(bytes);
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
                throw new StorageCorruptionException("Branch metadata ended before the requested bytes were read.");
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
                "Branch metadata is faulted after an uncertain I/O operation and must be reopened.");
        }
    }

    private void EnsureEventSequenceAvailable()
    {
        if (_nextEventSequence == ulong.MaxValue)
        {
            throw new StorageLimitException("Branch metadata event sequence space is exhausted.");
        }
    }

    private static ulong NextEventSequence(ulong current)
    {
        if (current == ulong.MaxValue)
        {
            throw new StorageCorruptionException("Branch metadata event sequence overflows persistent storage.");
        }
        return current + 1;
    }
}
