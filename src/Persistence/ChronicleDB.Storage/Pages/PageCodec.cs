using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage.Formats;

namespace ChronicleDB.Storage.Pages;

public static class PageCodec
{
    public const int Size = 32;

    private static ReadOnlySpan<byte> Magic => "CPG1"u8;

    public static byte[] Encode(
        PageHeader header,
        ReadOnlySpan<byte> payload,
        int pageSize)
    {
        if (pageSize != StorageOptions.DefaultPageSize)
        {
            throw new StorageFormatException("The v0.1 page size must be 16 KiB.");
        }

        if (!header.PageId.IsValid)
        {
            throw new StorageFormatException("A page must have a valid one-based page ID.");
        }

        if (header.Generation == 0)
        {
            throw new StorageFormatException("A page must have a non-zero generation marker.");
        }

        if (!Enum.IsDefined(header.Type))
        {
            throw new StorageFormatException("Unknown page type.");
        }

        if (payload.Length > pageSize - Size)
        {
            throw new StorageLimitException("Page payload exceeds the configured page capacity.");
        }

        if (payload.Length > ushort.MaxValue)
        {
            throw new StorageLimitException("Page payload exceeds the encoded length field.");
        }

        if (header.PayloadLength != payload.Length)
        {
            throw new StorageFormatException("Page header payload length does not match the encoded payload.");
        }

        var page = new byte[pageSize];
        Magic.CopyTo(page);
        page[4] = (byte)header.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(6, 2), Size);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(8, 8), header.PageId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(16, 8), header.Generation);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(24, 2), checked((ushort)payload.Length));
        payload.CopyTo(page.AsSpan(Size));
        BinaryPrimitives.WriteUInt32LittleEndian(
            page.AsSpan(28, 4),
            Crc32C.ComputeWithZeroedRange(page, 28, 4));
        return page;
    }

    public static DecodedPage Decode(ReadOnlySpan<byte> page, int pageSize)
    {
        if (pageSize != StorageOptions.DefaultPageSize || page.Length != pageSize)
        {
            throw new StorageCorruptionException("Page length does not match the configured page size.");
        }

        if (!page[..4].SequenceEqual(Magic))
        {
            throw new StorageCorruptionException("Page magic is invalid.");
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(page[28..32]);
        var actualChecksum = Crc32C.ComputeWithZeroedRange(page, 28, 4);
        if (expectedChecksum != actualChecksum)
        {
            throw new StorageCorruptionException("Page checksum is invalid.");
        }

        var typeValue = page[4];
        if (!Enum.IsDefined((PageType)typeValue))
        {
            throw new StorageCorruptionException("Page type is invalid.");
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(page[6..8]);
        var pageIdValue = BinaryPrimitives.ReadUInt64LittleEndian(page[8..16]);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(page[16..24]);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(page[24..26]);
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(page[26..28]);

        if (page[5] != 0 || headerSize != Size || pageIdValue == 0 || generation == 0 || reserved != 0)
        {
            throw new StorageCorruptionException("Page header contains invalid fields.");
        }

        if (payloadLength > pageSize - Size)
        {
            throw new StorageCorruptionException("Page payload length exceeds page capacity.");
        }

        if (!page[(Size + payloadLength)..].IsEmpty && page[(Size + payloadLength)..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new StorageCorruptionException("Page padding contains non-zero bytes.");
        }

        return new DecodedPage(
            new PageHeader(
                new PageId(pageIdValue),
                (PageType)typeValue,
                generation,
                payloadLength),
            page.Slice(Size, payloadLength).ToArray());
    }
}

public sealed record DecodedPage(PageHeader Header, byte[] Payload);
