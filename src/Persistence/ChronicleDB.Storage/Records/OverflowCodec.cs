using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Storage.Records;

internal static class OverflowCodec
{
    public const int HeaderSize = 16;

    public static byte[] Encode(PageId nextPage, ReadOnlySpan<byte> chunk, int pageSize)
    {
        var payloadCapacity = checked(pageSize - Pages.PageHeader.Size);
        if (chunk.Length > payloadCapacity - HeaderSize)
        {
            throw new StorageLimitException("Overflow chunk exceeds page capacity.");
        }

        var payload = new byte[HeaderSize + chunk.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), nextPage.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), checked((uint)chunk.Length));
        chunk.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    public static DecodedOverflow Decode(ReadOnlySpan<byte> payload, int pageSize)
    {
        if (payload.Length < HeaderSize)
        {
            throw new StorageCorruptionException("Overflow payload is shorter than its header.");
        }

        var nextPageValue = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
        var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]);
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]);
        var maximumPayload = checked(pageSize - Pages.PageHeader.Size);

        if (reserved != 0 || chunkLength > maximumPayload - HeaderSize || HeaderSize + chunkLength != payload.Length)
        {
            throw new StorageCorruptionException("Overflow payload fields are invalid.");
        }

        return new DecodedOverflow(
            new PageId(nextPageValue),
            payload[HeaderSize..].ToArray());
    }
}

internal sealed record DecodedOverflow(PageId NextPage, byte[] Chunk);
