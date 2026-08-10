using System.Buffers.Binary;
using System.Text;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.Snapshots;

public static class SnapshotStoreRecordCodec
{
    public const int HeaderSize = 64;
    public const int FooterSize = 8;
    public const int MinimumRecordSize = HeaderSize + FooterSize;
    public const int MaximumRecordSize = HeaderSize + (int)SnapshotStoreHeader.MaxNameBytes + FooterSize;

    private const byte CurrentVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Magic => "SNP1"u8;
    private static ReadOnlySpan<byte> FooterMagic => "SEND"u8;

    public static byte[] Encode(SnapshotStoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIdentity(record);

        byte[] nameBytes;
        switch (record.Type)
        {
            case SnapshotStoreRecordType.Create:
                ArgumentException.ThrowIfNullOrWhiteSpace(record.Name);
                if (!string.Equals(record.Name, record.Name.Trim(), StringComparison.Ordinal))
                {
                    throw new StorageFormatException("Snapshot names may not contain leading or trailing whitespace.");
                }

                if (record.CreatedUnixMilliseconds < 0
                    || record.CreatedUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                {
                    throw new StorageFormatException("Snapshot creation time is outside the supported UTC range.");
                }

                try
                {
                    nameBytes = Utf8.GetBytes(record.Name);
                }
                catch (EncoderFallbackException exception)
                {
                    throw new StorageFormatException("Snapshot name must be valid UTF-8 text.", exception);
                }

                if (nameBytes.Length > SnapshotStoreHeader.MaxNameBytes)
                {
                    throw new StorageLimitException("Snapshot name exceeds the persistent UTF-8 byte limit.");
                }

                break;
            case SnapshotStoreRecordType.Delete:
                if (!record.Sequence.IsInitial || record.CreatedUnixMilliseconds != 0 || record.Name.Length != 0)
                {
                    throw new StorageFormatException("Snapshot delete records may contain only identity metadata.");
                }

                nameBytes = [];
                break;
            default:
                throw new StorageFormatException("Snapshot record type is not supported.");
        }

        var totalLength = checked(HeaderSize + nameBytes.Length + FooterSize);
        var buffer = new byte[totalLength];
        Magic.CopyTo(buffer);
        buffer[4] = CurrentVersion;
        buffer[5] = (byte)record.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)totalLength));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), checked((uint)totalLength));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), record.EventSequence);
        record.SnapshotId.Value.TryWriteBytes(buffer.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(40, 8), record.Sequence.Value);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(48, 8), record.CreatedUnixMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(56, 4), checked((uint)nameBytes.Length));
        nameBytes.CopyTo(buffer.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(totalLength - FooterSize, 4), checked((uint)totalLength));
        FooterMagic.CopyTo(buffer.AsSpan(totalLength - 4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(60, 4),
            Crc32C.ComputeWithZeroedRange(buffer, 60, 4));
        return buffer;
    }

    public static SnapshotStoreRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinimumRecordSize || bytes.Length > MaximumRecordSize)
        {
            throw new StorageCorruptionException("Snapshot record length is outside the supported range.");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new StorageFormatException("Snapshot record magic is not recognized.");
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..64]);
        var actualChecksum = Crc32C.ComputeWithZeroedRange(bytes, 60, 4);
        if (expectedChecksum != actualChecksum)
        {
            throw new StorageCorruptionException("Snapshot record checksum is invalid.");
        }

        var version = bytes[4];
        var typeValue = bytes[5];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var repeatedLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var eventSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..24]);
        var snapshotId = new SnapshotId(new Guid(bytes[24..40]));
        var sequence = new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..48]));
        var created = BinaryPrimitives.ReadInt64LittleEndian(bytes[48..56]);
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..60]);

        if (version != CurrentVersion || flags != 0 || headerSize != HeaderSize
            || repeatedLength != totalLength || totalLength != bytes.Length
            || eventSequence == 0 || !snapshotId.IsValid)
        {
            throw new StorageFormatException("Snapshot record header contains invalid or unsupported fields.");
        }

        if (nameLength > SnapshotStoreHeader.MaxNameBytes
            || HeaderSize + nameLength + FooterSize != totalLength)
        {
            throw new StorageCorruptionException("Snapshot record name length does not match the record frame.");
        }

        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^FooterSize..^4]);
        if (footerLength != totalLength || !bytes[^4..].SequenceEqual(FooterMagic))
        {
            throw new StorageCorruptionException("Snapshot record footer is invalid.");
        }

        if (!Enum.IsDefined((SnapshotStoreRecordType)typeValue))
        {
            throw new StorageFormatException("Snapshot record type is not supported.");
        }

        string name;
        try
        {
            name = Utf8.GetString(bytes.Slice(HeaderSize, checked((int)nameLength)));
        }
        catch (DecoderFallbackException exception)
        {
            throw new StorageCorruptionException("Snapshot name contains invalid UTF-8.", exception);
        }

        var record = new SnapshotStoreRecord(
            (SnapshotStoreRecordType)typeValue,
            eventSequence,
            snapshotId,
            sequence,
            created,
            name);
        ValidateDecodedSemantics(record, nameLength);
        return record;
    }

    private static void ValidateIdentity(SnapshotStoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record.Name);
        if (!Enum.IsDefined(record.Type) || record.EventSequence == 0 || !record.SnapshotId.IsValid)
        {
            throw new StorageFormatException("Snapshot record identity fields are invalid.");
        }
    }

    private static void ValidateDecodedSemantics(SnapshotStoreRecord record, uint nameLength)
    {
        switch (record.Type)
        {
            case SnapshotStoreRecordType.Create:
                if (record.Name.Length == 0
                    || nameLength == 0
                    || record.CreatedUnixMilliseconds < 0
                    || record.CreatedUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                    || string.IsNullOrWhiteSpace(record.Name)
                    || !string.Equals(record.Name, record.Name.Trim(), StringComparison.Ordinal))
                {
                    throw new StorageCorruptionException("Snapshot create record metadata is invalid.");
                }

                break;
            case SnapshotStoreRecordType.Delete:
                if (!record.Sequence.IsInitial || record.CreatedUnixMilliseconds != 0
                    || nameLength != 0 || record.Name.Length != 0)
                {
                    throw new StorageCorruptionException("Snapshot delete record contains unexpected metadata.");
                }

                break;
            default:
                throw new StorageFormatException("Snapshot record type is not supported.");
        }
    }
}
