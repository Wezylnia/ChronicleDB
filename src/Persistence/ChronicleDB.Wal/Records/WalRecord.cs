using ChronicleDB.Core.Identifiers;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Formats;

namespace ChronicleDB.Wal.Records;

public sealed class WalRecord
{
    private readonly byte[] _payload;

    public WalRecord(
        WalRecordType type,
        ulong lsn,
        TransactionId transactionId,
        ReadOnlySpan<byte> payload,
        ushort flags = 0)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        ArgumentOutOfRangeException.ThrowIfZero(lsn);

        if (!transactionId.IsValid)
        {
            throw new ArgumentException("A WAL record requires a non-empty transaction ID.", nameof(transactionId));
        }

        if (flags != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        _payload = payload.ToArray();
        if (_payload.Length > WalRecordCodec.MaxPayloadSize)
        {
            throw new WalLimitException("WAL payload exceeds the maximum supported size.");
        }

        Type = type;
        Lsn = lsn;
        TransactionId = transactionId;
        Flags = flags;
    }

    public WalRecordType Type { get; }

    public ulong Lsn { get; }

    public TransactionId TransactionId { get; }

    public ushort Flags { get; }

    public ReadOnlyMemory<byte> Payload => _payload;
}
