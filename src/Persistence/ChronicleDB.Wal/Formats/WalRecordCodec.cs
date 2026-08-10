using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.Wal.Formats;

internal static class WalRecordCodec
{
    public const int HeaderSize = 48;
    // Mutation values are limited to 64 MiB, but a Put payload also contains
    // its key and length fields. Keep the record envelope slightly larger so
    // every otherwise-valid 64 MiB value + 64 KiB key can be represented.
    public const int MaxPayloadSize = 65 * 1024 * 1024;
    private const byte LegacyVersion = 1;
    private const byte CurrentVersion = 2;
    private const ushort HeaderLength = HeaderSize;
    private const uint LegacyCrc32CAlgorithm = 1;

    private static ReadOnlySpan<byte> Magic => "CWL1"u8;

    public static byte[] Encode(WalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Lsn == 0 || !record.TransactionId.IsValid)
        {
            throw new WalFormatException("WAL records require a non-zero LSN and transaction ID.");
        }

        if (!Enum.IsDefined(record.Type) || record.Flags != 0)
        {
            throw new WalFormatException("WAL record type or flags are not supported.");
        }

        if (record.Payload.Length > MaxPayloadSize)
        {
            throw new WalLimitException("WAL payload exceeds the maximum supported size.");
        }

        var payloadLength = checked((uint)record.Payload.Length);
        var totalLength = checked(HeaderSize + record.Payload.Length);
        var buffer = new byte[totalLength];
        Magic.CopyTo(buffer);
        buffer[4] = CurrentVersion;
        buffer[5] = (byte)record.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), record.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), HeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(12, 8), record.Lsn);
        record.TransactionId.Value.TryWriteBytes(buffer.AsSpan(20, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36, 4), payloadLength);
        // v2 uses the former checksum-algorithm slot as an independently readable
        // complement of payload length. The checksum algorithm is fixed by record
        // version, allowing the scanner to distinguish an internally inconsistent
        // complete header from a legitimate crash-truncated payload.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), ~payloadLength);
        record.Payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(44, 4),
            Crc32C.ComputeWithZeroedRange(buffer, 44, 4));
        return buffer;
    }

    public static WalRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            throw new WalCorruptionException("WAL record is shorter than its header.");
        }

        var payloadLength = ReadValidatedPayloadLengthForScan(bytes[..HeaderSize]);
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..48]);
        var actualChecksum = Crc32C.ComputeWithZeroedRange(bytes, 44, 4);
        if (expectedChecksum != actualChecksum)
        {
            throw new WalCorruptionException("WAL record checksum is invalid.");
        }

        var totalLength = checked(HeaderSize + (int)payloadLength);
        if (bytes.Length != totalLength)
        {
            throw new WalCorruptionException("WAL record length does not match its payload field.");
        }

        var type = (WalRecordType)bytes[5];
        var lsn = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..20]);
        var transactionId = new TransactionId(new Guid(bytes[20..36]));
        return new WalRecord(type, lsn, transactionId, bytes[HeaderSize..]);
    }

    internal static uint ReadValidatedPayloadLengthForScan(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
        {
            throw new WalCorruptionException("WAL record header has an invalid length.");
        }

        if (!header[..4].SequenceEqual(Magic))
        {
            throw new WalFormatException("WAL record magic is not recognized.");
        }

        var version = header[4];
        var typeValue = header[5];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]);
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]);
        var lsn = BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]);
        var transactionId = new TransactionId(new Guid(header[20..36]));
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]);
        var versionField = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);

        if (version is not (LegacyVersion or CurrentVersion)
            || headerLength != HeaderLength
            || reserved != 0)
        {
            throw new WalFormatException("WAL record header contains unsupported fields.");
        }

        if (!Enum.IsDefined((WalRecordType)typeValue))
        {
            throw new WalFormatException("WAL record type is not supported.");
        }

        if (flags != 0)
        {
            throw new WalFormatException("WAL record flags are not supported.");
        }

        if (lsn == 0 || !transactionId.IsValid)
        {
            throw new WalFormatException("WAL record identity fields are invalid.");
        }

        if (payloadLength > MaxPayloadSize)
        {
            throw new WalLimitException("WAL payload exceeds the maximum supported size.");
        }

        if (version == LegacyVersion)
        {
            if (versionField != LegacyCrc32CAlgorithm)
            {
                throw new WalFormatException("Legacy WAL checksum algorithm is not supported.");
            }
        }
        else if (versionField != ~payloadLength)
        {
            throw new WalCorruptionException("WAL record payload-length redundancy check failed.");
        }

        return payloadLength;
    }
}
