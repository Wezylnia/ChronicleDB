using System.Buffers.Binary;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Wal.Errors;

namespace ChronicleDB.Wal.Records;

/// <summary>
/// Encodes the v0.3 logical commit sequence and the physical data-file length
/// observed before publication. The latter lets recovery distinguish the newest
/// redoable append region from older persistent corruption.
/// </summary>
public static class WalCommitCodec
{
    public const int LegacySequenceOnlyPayloadSize = sizeof(ulong);
    public const int PayloadSize = sizeof(ulong) + sizeof(ulong);

    public static byte[] Encode(CommitSequence commitSequence, long baseDataLength)
    {
        if (commitSequence.IsInitial)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commitSequence),
                "A WAL commit requires a non-zero commit sequence.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(baseDataLength);

        var payload = new byte[PayloadSize];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), commitSequence.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), checked((ulong)baseDataLength));
        return payload;
    }

    public static WalCommitInfo Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is not (LegacySequenceOnlyPayloadSize or PayloadSize))
        {
            throw new WalCorruptionException(
                "WAL commit payload must contain a commit sequence and optional recovery base.");
        }

        var sequence = new CommitSequence(BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]));
        if (sequence.IsInitial)
        {
            throw new WalCorruptionException("WAL commit sequence must be non-zero.");
        }

        if (payload.Length == LegacySequenceOnlyPayloadSize)
        {
            return new WalCommitInfo(sequence, null);
        }

        var baseDataLengthValue = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..16]);
        if (baseDataLengthValue > long.MaxValue)
        {
            throw new WalCorruptionException("WAL recovery base exceeds the supported file-length range.");
        }

        return new WalCommitInfo(sequence, checked((long)baseDataLengthValue));
    }
}

public readonly record struct WalCommitInfo(
    CommitSequence CommitSequence,
    long? BaseDataLength);
