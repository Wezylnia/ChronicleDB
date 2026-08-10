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
    private const byte CurrentVersion = 1;
    private const ushort HeaderLength = HeaderSize;
    private const uint Crc32CAlgorithm = 1;

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

        var totalLength = checked(HeaderSize + record.Payload.Length);
        var buffer = new byte[totalLength];
        Magic.CopyTo(buffer);
        buffer[4] = CurrentVersion;
        buffer[5] = (byte)record.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), record.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), HeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(12, 8), record.Lsn);
        record.TransactionId.Value.TryWriteBytes(buffer.AsSpan(20, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36, 4), checked((uint)record.Payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), Crc32CAlgorithm);
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

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new WalFormatException("WAL record magic is not recognized.");
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..48]);
        var actualChecksum = Crc32C.ComputeWithZeroedRange(bytes, 44, 4);
        if (expectedChecksum != actualChecksum)
        {
            throw new WalCorruptionException("WAL record checksum is invalid.");
        }

        var version = bytes[4];
        var typeValue = bytes[5];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        var lsn = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..20]);
        var transactionId = new TransactionId(new Guid(bytes[20..36]));
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..40]);
        var checksumAlgorithm = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..44]);

        if (version != CurrentVersion || headerLength != HeaderLength || reserved != 0 || checksumAlgorithm != Crc32CAlgorithm)
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

        var totalLength = checked(HeaderSize + (int)payloadLength);
        if (bytes.Length != totalLength)
        {
            throw new WalCorruptionException("WAL record length does not match its payload field.");
        }

        return new WalRecord(
            (WalRecordType)typeValue,
            lsn,
            transactionId,
            bytes[HeaderSize..]);
    }
}
