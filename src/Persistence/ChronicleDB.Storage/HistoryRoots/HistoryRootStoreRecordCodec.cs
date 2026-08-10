using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.HistoryRoots;

public static class HistoryRootStoreRecordCodec
{
    public const int HeaderSize = 112;
    public const int FooterSize = 8;
    public const int RecordSize = HeaderSize + FooterSize;

    private const byte CurrentVersion = 1;
    private static ReadOnlySpan<byte> Magic => "HRT1"u8;
    private static ReadOnlySpan<byte> FooterMagic => "HEND"u8;

    public static byte[] Encode(HistoryRootStoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        var buffer = new byte[RecordSize];
        Magic.CopyTo(buffer);
        buffer[4] = CurrentVersion;
        buffer[5] = (byte)record.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), record.EventSequence);
        record.RootId.Value.TryWriteBytes(buffer.AsSpan(24, 16));
        buffer[40] = record.RootKind;
        buffer[41] = record.RootState;
        record.OwnerDatabaseId.TryWriteBytes(buffer.AsSpan(44, 16));
        record.HistoryId.Value.TryWriteBytes(buffer.AsSpan(60, 16));
        record.ParentHistoryId.Value.TryWriteBytes(buffer.AsSpan(76, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(92, 8), record.Boundary.Value);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(100, 8), record.CreatedUnixMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(112, 4), RecordSize);
        FooterMagic.CopyTo(buffer.AsSpan(116, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(108, 4),
            Crc32C.ComputeWithZeroedRange(buffer, 108, 4));
        return buffer;
    }

    public static HistoryRootStoreRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordSize)
        {
            throw new StorageCorruptionException("History-root record length is invalid.");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new StorageFormatException("History-root record magic is not recognized.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[108..112]);
        if (expected != Crc32C.ComputeWithZeroedRange(bytes, 108, 4))
        {
            throw new StorageCorruptionException("History-root record checksum is invalid.");
        }

        var typeValue = bytes[5];
        var version = bytes[4];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var repeatedLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var eventSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..24]);
        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[112..116]);
        if (version != CurrentVersion || flags != 0 || headerSize != HeaderSize
            || repeatedLength != RecordSize || totalLength != RecordSize
            || eventSequence == 0 || footerLength != RecordSize
            || !bytes[116..120].SequenceEqual(FooterMagic)
            || !Enum.IsDefined((HistoryRootStoreRecordType)typeValue))
        {
            throw new StorageFormatException("History-root record framing or identity is invalid.");
        }

        var record = new HistoryRootStoreRecord(
            (HistoryRootStoreRecordType)typeValue,
            eventSequence,
            new HistoryRootId(new Guid(bytes[24..40])),
            bytes[40],
            bytes[41],
            new Guid(bytes[44..60]),
            new HistoryId(new Guid(bytes[60..76])),
            new HistoryId(new Guid(bytes[76..92])),
            new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(bytes[92..100])),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[100..108]));
        Validate(record);
        return record;
    }

    private static void Validate(HistoryRootStoreRecord record)
    {
        if (!Enum.IsDefined(record.Type)
            || record.EventSequence == 0
            || !record.RootId.IsValid
            || record.RootKind == 0
            || record.RootState == 0
            || record.OwnerDatabaseId == Guid.Empty
            || !record.HistoryId.IsValid
            || record.CreatedUnixMilliseconds < 0
            || record.CreatedUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            throw new StorageFormatException("History-root record identity or metadata is invalid.");
        }

        if (record.Type == HistoryRootStoreRecordType.Create
            && record.RootState != 2)
        {
            throw new StorageFormatException("History-root create records must publish the Active state.");
        }

        if (record.RootKind == 2
            && (!record.ParentHistoryId.IsValid || record.ParentHistoryId == record.HistoryId))
        {
            throw new StorageFormatException(
                "Branch-base history-root records require a distinct valid parent history.");
        }

        if (record.RootKind != 2 && record.ParentHistoryId.IsValid)
        {
            throw new StorageFormatException(
                "Only branch-base history-root records may identify a parent history.");
        }

        if (record.Type == HistoryRootStoreRecordType.Delete && record.RootState != 4)
        {
            throw new StorageFormatException("History-root delete record metadata is invalid.");
        }
    }
}
