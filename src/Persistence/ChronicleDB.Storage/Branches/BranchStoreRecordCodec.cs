using System.Buffers.Binary;
using System.Text;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.Branches;

public static class BranchStoreRecordCodec
{
    public const int HeaderSize = 168;
    public const int FooterSize = 8;
    public const int MaxNameBytes = 1024;
    private const byte CurrentVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "BRN1"u8;
    private static ReadOnlySpan<byte> FooterMagic => "BEND"u8;

    public static byte[] Encode(BranchStoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        var nameBytes = StrictUtf8.GetBytes(record.Name);
        var totalLength = checked(HeaderSize + nameBytes.Length + FooterSize);
        var buffer = new byte[totalLength];
        Magic.CopyTo(buffer);
        buffer[4] = CurrentVersion;
        buffer[5] = (byte)record.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)totalLength));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), checked((uint)totalLength));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), record.EventSequence);
        record.BranchId.Value.TryWriteBytes(buffer.AsSpan(24, 16));
        record.HistoryId.Value.TryWriteBytes(buffer.AsSpan(40, 16));
        record.ParentHistoryId.Value.TryWriteBytes(buffer.AsSpan(56, 16));
        record.BaseRootId.Value.TryWriteBytes(buffer.AsSpan(72, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(88, 8), record.ParentBaseSequence.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(96, 8), record.LocalCommitSequence.Value);
        record.LocalStorageId.TryWriteBytes(buffer.AsSpan(104, 16));
        record.TransactionId.Value.TryWriteBytes(buffer.AsSpan(120, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(136, 4), checked((uint)record.MutationCount));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(140, 8), record.DataLengthAfterCommit);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(148, 8), record.CreatedUnixMilliseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(156, 2), checked((ushort)record.Depth));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(158, 2), checked((ushort)nameBytes.Length));
        nameBytes.CopyTo(buffer.AsSpan(HeaderSize, nameBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(totalLength - FooterSize, 4), checked((uint)totalLength));
        FooterMagic.CopyTo(buffer.AsSpan(totalLength - 4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(160, 4),
            Crc32C.ComputeWithZeroedRange(buffer, 160, 4));
        return buffer;
    }

    public static BranchStoreRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize + FooterSize || !bytes[..4].SequenceEqual(Magic))
        {
            throw new StorageFormatException("Branch metadata record framing is invalid.");
        }

        var version = bytes[4];
        var typeValue = bytes[5];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var repeatedLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var eventSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..24]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[158..160]);
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[160..164]);
        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^8..^4]);
        if (version != CurrentVersion
            || flags != 0
            || headerSize != HeaderSize
            || repeatedLength != (ushort)bytes.Length
            || totalLength != (uint)bytes.Length
            || eventSequence == 0
            || !Enum.IsDefined((BranchStoreRecordType)typeValue)
            || nameLength > MaxNameBytes
            || HeaderSize + nameLength + FooterSize != bytes.Length
            || expectedChecksum != Crc32C.ComputeWithZeroedRange(bytes, 160, 4)
            || footerLength != (uint)bytes.Length
            || !bytes[^4..].SequenceEqual(FooterMagic)
            || bytes[164..168].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new StorageCorruptionException("Branch metadata record header, checksum, or footer is invalid.");
        }

        string name;
        try
        {
            name = StrictUtf8.GetString(bytes.Slice(HeaderSize, nameLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new StorageFormatException("Branch name metadata is not valid UTF-8.", exception);
        }

        var mutationCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[136..140]);
        if (mutationCount > int.MaxValue)
        {
            throw new StorageCorruptionException("Branch metadata mutation count is outside the supported range.");
        }

        var record = new BranchStoreRecord(
            (BranchStoreRecordType)typeValue,
            eventSequence,
            new BranchId(new Guid(bytes[24..40])),
            new HistoryId(new Guid(bytes[40..56])),
            new HistoryId(new Guid(bytes[56..72])),
            new HistoryRootId(new Guid(bytes[72..88])),
            new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(bytes[88..96])),
            new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(bytes[96..104])),
            new Guid(bytes[104..120]),
            new TransactionId(new Guid(bytes[120..136])),
            (int)mutationCount,
            BinaryPrimitives.ReadInt64LittleEndian(bytes[140..148]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[148..156]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[156..158]),
            name);
        Validate(record);
        return record;
    }

    private static void Validate(BranchStoreRecord record)
    {
        if (!Enum.IsDefined(record.Type)
            || record.EventSequence == 0
            || !record.BranchId.IsValid
            || !record.HistoryId.IsValid
            || !record.ParentHistoryId.IsValid
            || record.HistoryId == record.ParentHistoryId
            || !record.BaseRootId.IsValid
            || record.CreatedUnixMilliseconds < 0
            || record.CreatedUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            || record.Depth is < 1 or > 16)
        {
            throw new StorageFormatException("Branch metadata identity or ancestry is invalid.");
        }

        if (string.IsNullOrWhiteSpace(record.Name)
            || !string.Equals(record.Name, record.Name.Trim(), StringComparison.Ordinal))
        {
            throw new StorageFormatException("Branch metadata name is invalid.");
        }

        int nameByteCount;
        try
        {
            nameByteCount = StrictUtf8.GetByteCount(record.Name);
        }
        catch (EncoderFallbackException exception)
        {
            throw new StorageFormatException("Branch metadata name is not valid UTF-8 text.", exception);
        }

        if (nameByteCount > MaxNameBytes)
        {
            throw new StorageFormatException("Branch metadata name is invalid.");
        }

        switch (record.Type)
        {
            case BranchStoreRecordType.CreateIntent:
            case BranchStoreRecordType.AbandonCreate:
                if (!record.LocalCommitSequence.IsInitial
                    || record.LocalStorageId != Guid.Empty
                    || record.TransactionId.IsValid
                    || record.MutationCount != 0
                    || record.DataLengthAfterCommit != 0)
                {
                    throw new StorageFormatException("Branch creation metadata contains invalid local-history fields.");
                }
                break;
            case BranchStoreRecordType.Activate:
                if (!record.LocalCommitSequence.IsInitial
                    || record.LocalStorageId == Guid.Empty
                    || record.TransactionId.IsValid
                    || record.MutationCount != 0
                    || record.DataLengthAfterCommit != 0)
                {
                    throw new StorageFormatException("Branch activation metadata is invalid.");
                }
                break;
            case BranchStoreRecordType.AdvanceSequence:
                if (record.LocalCommitSequence.IsInitial
                    || record.LocalStorageId == Guid.Empty
                    || !record.TransactionId.IsValid
                    || record.MutationCount < 0
                    || record.DataLengthAfterCommit < 0)
                {
                    throw new StorageFormatException("Branch commit metadata is invalid.");
                }
                break;
            default:
                throw new StorageFormatException("Branch metadata record type is unsupported.");
        }
    }
}
