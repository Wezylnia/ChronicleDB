using System.Buffers.Binary;
using ChronicleDB.Core.Keys;
using ChronicleDB.Wal.Errors;

namespace ChronicleDB.Wal.Records;

public static class WalMutationCodec
{
    public const int KeyLengthSize = 2;
    public const int ValueLengthSize = 4;
    public const int PutHeaderSize = KeyLengthSize + ValueLengthSize;
    public const int MaxKeySize = ushort.MaxValue;
    public const int MaxValueSize = 64 * 1024 * 1024;

    public static byte[] EncodePut(BinaryKey key, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateKey(key);
        ValidateValue(value);

        var payload = new byte[checked(PutHeaderSize + key.Length + value.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)key.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), checked((uint)value.Length));
        key.AsSpan().CopyTo(payload.AsSpan(PutHeaderSize));
        value.CopyTo(payload.AsSpan(PutHeaderSize + key.Length));
        return payload;
    }

    public static byte[] EncodeDelete(BinaryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateKey(key);

        var payload = new byte[checked(KeyLengthSize + key.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)key.Length));
        key.AsSpan().CopyTo(payload.AsSpan(KeyLengthSize));
        return payload;
    }

    public static WalMutation DecodePut(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PutHeaderSize)
        {
            throw new WalCorruptionException("WAL put payload is shorter than its header.");
        }

        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..6]);
        ValidateDecodedLengths(payload.Length, keyLength, valueLength, PutHeaderSize);
        var key = new BinaryKey(payload.Slice(PutHeaderSize, keyLength));
        var value = payload.Slice(PutHeaderSize + keyLength, checked((int)valueLength));
        return new WalMutation(key, isDelete: false, value);
    }

    public static WalMutation DecodeDelete(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < KeyLengthSize)
        {
            throw new WalCorruptionException("WAL delete payload is shorter than its header.");
        }

        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        if (payload.Length != KeyLengthSize + keyLength)
        {
            throw new WalCorruptionException("WAL delete payload length does not match its key field.");
        }

        return new WalMutation(new BinaryKey(payload[KeyLengthSize..]), isDelete: true, ReadOnlySpan<byte>.Empty);
    }

    private static void ValidateKey(BinaryKey key)
    {
        if (key.Length > MaxKeySize)
        {
            throw new WalLimitException("WAL mutation key exceeds the encoded limit.");
        }
    }

    private static void ValidateValue(ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxValueSize)
        {
            throw new WalLimitException("WAL mutation value exceeds the encoded limit.");
        }
    }

    private static void ValidateDecodedLengths(int payloadLength, ushort keyLength, uint valueLength, int headerSize)
    {
        if (valueLength > MaxValueSize || payloadLength != checked(headerSize + keyLength + (int)valueLength))
        {
            throw new WalCorruptionException("WAL put payload lengths are invalid.");
        }
    }
}
