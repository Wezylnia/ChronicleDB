using ChronicleDB.Core.Identifiers;
using ChronicleDB.Wal.Errors;

namespace ChronicleDB.Wal.Branches;

/// <summary>
/// Binds every branch WAL payload to both the branch identity and its independently
/// evolving history domain. The ordinary WAL file header remains bound to the
/// branch-local storage database ID; this envelope prevents a valid record stream
/// from being replayed into a sibling branch or a different history domain.
/// </summary>
public static class BranchWalEnvelopeCodec
{
    public const int PrefixSize = 40;
    private const byte CurrentVersion = 1;
    private static ReadOnlySpan<byte> Magic => "BWV1"u8;

    public static byte[] Encode(BranchId branchId, HistoryId historyId, ReadOnlySpan<byte> payload)
    {
        if (!branchId.IsValid || !historyId.IsValid)
        {
            throw new ArgumentException("Branch WAL records require valid branch and history identities.");
        }

        var encoded = new byte[checked(PrefixSize + payload.Length)];
        Magic.CopyTo(encoded);
        encoded[4] = CurrentVersion;
        // 5..7 reserved and deliberately zero.
        branchId.Value.TryWriteBytes(encoded.AsSpan(8, 16));
        historyId.Value.TryWriteBytes(encoded.AsSpan(24, 16));
        payload.CopyTo(encoded.AsSpan(PrefixSize));
        return encoded;
    }

    public static BranchWalEnvelope Decode(
        ReadOnlySpan<byte> payload,
        BranchId expectedBranchId,
        HistoryId expectedHistoryId)
    {
        var decoded = Decode(payload);
        if (decoded.BranchId != expectedBranchId || decoded.HistoryId != expectedHistoryId)
        {
            throw new WalCorruptionException(
                "Branch WAL record belongs to another branch or history domain.");
        }
        return decoded;
    }

    public static BranchWalEnvelope Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PrefixSize || !payload[..4].SequenceEqual(Magic))
        {
            throw new WalCorruptionException("Branch WAL payload framing is invalid.");
        }

        if (payload[4] != CurrentVersion || payload.Slice(5, 3).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new WalCorruptionException("Branch WAL payload version or reserved bytes are invalid.");
        }

        var branchId = new BranchId(new Guid(payload.Slice(8, 16)));
        var historyId = new HistoryId(new Guid(payload.Slice(24, 16)));
        if (!branchId.IsValid || !historyId.IsValid)
        {
            throw new WalCorruptionException("Branch WAL payload contains an invalid logical identity.");
        }

        return new BranchWalEnvelope(branchId, historyId, payload[PrefixSize..].ToArray());
    }
}

public readonly record struct BranchWalEnvelope(
    BranchId BranchId,
    HistoryId HistoryId,
    ReadOnlyMemory<byte> Payload);
