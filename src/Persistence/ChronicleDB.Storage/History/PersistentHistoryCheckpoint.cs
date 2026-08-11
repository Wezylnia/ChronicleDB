using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Faults;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.History;

/// <summary>
/// Crash-safe copy-and-publish checkpoint for retained MVCC history. Checkpoints
/// are immutable complete files; a temporary file is fsynced before publication
/// and the previous generation remains recoverable until the new generation has
/// been validated on a later open.
/// </summary>
public static class PersistentHistoryCheckpoint
{
    public const string FileName = "chronicle.history";
    private const string BackupSuffix = ".previous";
    private const int HeaderSize = 80;
    private const int RecordHeaderSize = 64;
    private const ushort MajorVersion = 1;
    private const ushort MinorVersion = 0;
    private const byte TombstoneFlag = 1;
    private static ReadOnlySpan<byte> HeaderMagic => "CHHIST01"u8;
    private static ReadOnlySpan<byte> RecordMagic => "HVR1"u8;

    /// <summary>
    /// Reads the currently published primary checkpoint without performing recovery
    /// repair, backup promotion, or cleanup. Intended for diagnostics/research tools
    /// that must not mutate recovery authority while observing it.
    /// </summary>
    public static HistoryCheckpoint? Inspect(
        string directory,
        Guid expectedDatabaseId,
        HistoryId expectedHistoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (expectedDatabaseId == Guid.Empty || !expectedHistoryId.IsValid)
        {
            throw new ArgumentException("History checkpoint inspection requires valid database and history identities.");
        }

        var path = Path.Combine(Path.GetFullPath(directory), FileName);
        return File.Exists(path) ? Read(path, expectedDatabaseId, expectedHistoryId) : null;
    }

    public static HistoryCheckpoint? TryLoad(
        string directory,
        Guid expectedDatabaseId,
        HistoryId expectedHistoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (expectedDatabaseId == Guid.Empty || !expectedHistoryId.IsValid)
        {
            throw new ArgumentException("History checkpoint recovery requires valid database and history identities.");
        }

        var path = Path.Combine(Path.GetFullPath(directory), FileName);
        var backup = path + BackupSuffix;
        if (!File.Exists(path) && File.Exists(backup))
        {
            File.Move(backup, path, overwrite: true);
        }
        if (!File.Exists(path))
        {
            return null;
        }

        // A present primary is the only candidate for the current generation. A stale
        // .previous file may outlive successful publication if cleanup failed, and the
        // WAL may already have rotated past that older checkpoint. Falling back from a
        // corrupt present primary could therefore roll durable history backward. The
        // backup is used only above, when the primary is actually missing after an
        // interrupted rename.
        var result = Read(path, expectedDatabaseId, expectedHistoryId);
        TryDeleteNonAuthoritativeFile(backup);
        return result;
    }

    public static long Publish(
        string directory,
        HistoryCheckpoint checkpoint,
        IStorageFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateSemantic(checkpoint);

        faultInjector?.Hit(StorageFaultPoint.BeforeHistoryCheckpointWrite, PageId.Invalid);
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, FileName);
        var backup = path + BackupSuffix;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".creating";
        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       options: FileOptions.WriteThrough))
            {
                Write(stream, checkpoint, faultInjector);
                stream.Flush(flushToDisk: true);
            }
            faultInjector?.Hit(StorageFaultPoint.AfterHistoryCheckpointOutputFlush, PageId.Invalid);

            // Never discard the old authoritative checkpoint before the new complete
            // file exists. A crash between the two renames leaves .previous recoverable.
            if (File.Exists(path))
            {
                File.Move(path, backup, overwrite: true);
            }
            File.Move(temp, path, overwrite: true);

            // Re-read the published bytes before retiring the previous generation.
            _ = Read(path, checkpoint.DatabaseId, checkpoint.HistoryId);
            TryDeleteNonAuthoritativeFile(backup);
            return new FileInfo(path).Length;
        }
        finally
        {
            TryDeleteNonAuthoritativeFile(temp);
        }
    }

    private static void TryDeleteNonAuthoritativeFile(string path)
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
            // A validated primary remains authoritative. Cleanup can be retried later.
        }
    }

    private static void Write(
        Stream stream,
        HistoryCheckpoint checkpoint,
        IStorageFaultInjector? faultInjector)
    {
        var ordered = checkpoint.Versions
            .OrderBy(version => version.CommitSequence.Value)
            .ThenBy(version => version.Key, BinaryKeyLexicographicComparer.Instance)
            .ToArray();

        var header = new byte[HeaderSize];
        HeaderMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), HeaderSize);
        checkpoint.DatabaseId.TryWriteBytes(header.AsSpan(16, 16));
        checkpoint.HistoryId.Value.TryWriteBytes(header.AsSpan(32, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48, 8), checkpoint.CheckpointSequence.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56, 8), checkpoint.RetentionFloor.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64, 4), checked((uint)ordered.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), Crc32C.Compute(header.AsSpan(0, 76)));
        stream.Write(header);
        faultInjector?.Hit(StorageFaultPoint.AfterHistoryCheckpointHeaderWrite, PageId.Invalid);

        foreach (var version in ordered)
        {
            var key = version.Key.AsSpan();
            var value = version.IsDelete ? ReadOnlySpan<byte>.Empty : version.Value.Span;
            var totalLength = checked(RecordHeaderSize + key.Length + value.Length);
            var record = new byte[totalLength];
            RecordMagic.CopyTo(record);
            record[4] = 1;
            record[5] = version.IsDelete ? TombstoneFlag : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), RecordHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8, 4), checked((uint)totalLength));
            version.TransactionId.Value.TryWriteBytes(record.AsSpan(16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32, 8), version.CommitSequence.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40, 4), checked((uint)key.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44, 4), checked((uint)value.Length));
            key.CopyTo(record.AsSpan(RecordHeaderSize));
            value.CopyTo(record.AsSpan(RecordHeaderSize + key.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                record.AsSpan(12, 4),
                Crc32C.ComputeWithZeroedRange(record, 12, 4));
            stream.Write(record);
            faultInjector?.Hit(StorageFaultPoint.AfterHistoryCheckpointRecordWrite, PageId.Invalid);
        }
    }

    private static HistoryCheckpoint Read(
        string path,
        Guid expectedDatabaseId,
        HistoryId expectedHistoryId)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length < HeaderSize)
        {
            throw new StorageCorruptionException("History checkpoint header is truncated.");
        }

        var header = new byte[HeaderSize];
        ReadExactly(stream, header);
        if (!header.AsSpan(0, 8).SequenceEqual(HeaderMagic)
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)) != MajorVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2)) > MinorVersion
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4)) != HeaderSize
            || header.AsSpan(68, 8).IndexOfAnyExcept((byte)0) >= 0
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76, 4)) != Crc32C.Compute(header.AsSpan(0, 76)))
        {
            throw new StorageCorruptionException("History checkpoint header or checksum is invalid.");
        }

        var databaseId = new Guid(header.AsSpan(16, 16));
        var historyId = new HistoryId(new Guid(header.AsSpan(32, 16)));
        if (databaseId != expectedDatabaseId || historyId != expectedHistoryId)
        {
            throw new StorageCorruptionException("History checkpoint belongs to another database or history domain.");
        }

        var checkpointSequence = new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48, 8)));
        var retentionFloor = new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(56, 8)));
        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64, 4));
        var payloadBytes = stream.Length - HeaderSize;
        var maximumRecordCountByLength = payloadBytes / RecordHeaderSize;
        if (retentionFloor > checkpointSequence
            || recordCount > int.MaxValue
            || recordCount > maximumRecordCountByLength)
        {
            throw new StorageCorruptionException("History checkpoint sequence/count metadata is invalid.");
        }

        var count = checked((int)recordCount);
        // Do not trust a disk-declared count as an allocation request. The physical
        // length check above proves the count is structurally possible, but a sparse
        // or intentionally oversized corrupt file could still force an enormous
        // up-front allocation before the first record is validated. Grow gradually
        // while decoding instead.
        var versions = new List<HistoryCheckpointVersion>(Math.Min(count, 4_096));
        var recordHeader = new byte[RecordHeaderSize];
        for (var index = 0; index < count; index++)
        {
            ReadExactly(stream, recordHeader);
            if (!recordHeader.AsSpan(0, 4).SequenceEqual(RecordMagic)
                || recordHeader[4] != 1
                || (recordHeader[5] & ~TombstoneFlag) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(recordHeader.AsSpan(6, 2)) != RecordHeaderSize
                || recordHeader.AsSpan(48, 16).IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new StorageCorruptionException("History checkpoint version header is invalid.");
            }

            var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(8, 4));
            var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(40, 4));
            var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(44, 4));
            if (totalLength < RecordHeaderSize
                || totalLength > int.MaxValue
                || keyLength > ushort.MaxValue
                || valueLength > StorageOptions.AbsoluteMaxValueSize
                || totalLength != checked((uint)RecordHeaderSize + keyLength + valueLength))
            {
                throw new StorageCorruptionException("History checkpoint version lengths are invalid.");
            }

            var remainingPayloadBytes = stream.Length - stream.Position;
            var declaredPayloadBytes = checked((long)totalLength - RecordHeaderSize);
            if (declaredPayloadBytes > remainingPayloadBytes)
            {
                throw new StorageCorruptionException(
                    "History checkpoint version extends beyond the remaining file bytes.");
            }

            var encoded = new byte[checked((int)totalLength)];
            recordHeader.CopyTo(encoded, 0);
            ReadExactly(stream, encoded.AsSpan(RecordHeaderSize));
            if (BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(12, 4))
                != Crc32C.ComputeWithZeroedRange(encoded, 12, 4))
            {
                throw new StorageCorruptionException("History checkpoint version checksum is invalid.");
            }

            var isDelete = (encoded[5] & TombstoneFlag) != 0;
            if (isDelete && valueLength != 0)
            {
                throw new StorageCorruptionException("History checkpoint tombstone carries a value.");
            }
            var transactionId = new TransactionId(new Guid(encoded.AsSpan(16, 16)));
            var sequence = new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(32, 8)));
            if (!transactionId.IsValid || sequence.IsInitial || sequence > checkpointSequence)
            {
                throw new StorageCorruptionException("History checkpoint version identity or sequence is invalid.");
            }

            versions.Add(new HistoryCheckpointVersion(
                transactionId,
                sequence,
                new BinaryKey(encoded.AsSpan(RecordHeaderSize, checked((int)keyLength))),
                isDelete,
                encoded.AsMemory(RecordHeaderSize + checked((int)keyLength), checked((int)valueLength)).ToArray()));
        }

        if (stream.Position != stream.Length)
        {
            throw new StorageCorruptionException("History checkpoint contains unexplained trailing bytes.");
        }

        var checkpoint = new HistoryCheckpoint(
            databaseId,
            historyId,
            checkpointSequence,
            retentionFloor,
            versions);
        ValidateSemantic(checkpoint);
        return checkpoint;
    }

    private static void ValidateSemantic(HistoryCheckpoint checkpoint)
    {
        if (checkpoint.DatabaseId == Guid.Empty
            || !checkpoint.HistoryId.IsValid
            || checkpoint.RetentionFloor > checkpoint.CheckpointSequence)
        {
            throw new StorageFormatException("History checkpoint identity or sequence metadata is invalid.");
        }

        var transactionSequences = new Dictionary<TransactionId, CommitSequence>();
        var sequenceTransactions = new Dictionary<CommitSequence, TransactionId>();
        var keySequences = new HashSet<(BinaryKey Key, CommitSequence Sequence)>();
        foreach (var version in checkpoint.Versions)
        {
            ArgumentNullException.ThrowIfNull(version);
            if (!version.TransactionId.IsValid
                || version.CommitSequence.IsInitial
                || version.CommitSequence > checkpoint.CheckpointSequence
                || version.Key.Length > ushort.MaxValue
                || version.Value.Length > StorageOptions.AbsoluteMaxValueSize
                || version.IsDelete && !version.Value.IsEmpty)
            {
                throw new StorageFormatException("History checkpoint contains an invalid version.");
            }

            if (transactionSequences.TryGetValue(version.TransactionId, out var txSequence)
                && txSequence != version.CommitSequence)
            {
                throw new StorageFormatException("A checkpoint transaction identity spans multiple commit sequences.");
            }
            transactionSequences[version.TransactionId] = version.CommitSequence;

            if (sequenceTransactions.TryGetValue(version.CommitSequence, out var sequenceTx)
                && sequenceTx != version.TransactionId)
            {
                throw new StorageFormatException("A checkpoint commit sequence is assigned to multiple transactions.");
            }
            sequenceTransactions[version.CommitSequence] = version.TransactionId;

            if (!keySequences.Add((version.Key, version.CommitSequence)))
            {
                throw new StorageFormatException("A checkpoint contains duplicate key/version entries.");
            }
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
        => ReadExactly(stream, buffer.AsSpan());

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                throw new StorageCorruptionException("History checkpoint ended before the expected bytes were read.");
            }
            read += count;
        }
    }
}
