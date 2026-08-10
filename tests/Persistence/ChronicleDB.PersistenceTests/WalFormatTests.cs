using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Formats;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.PersistenceTests;

public sealed class WalFormatTests
{
    [Fact]
    public void Crc32CKnownVectorMatchesTheStandardCheckValue()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
    }

    [Fact]
    public void PutRecordHasStableGoldenEncoding()
    {
        var record = new WalRecord(
            WalRecordType.Put,
            7,
            new TransactionId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            [1, 2, 3]);

        var encoded = WalRecordCodec.Encode(record);

        Assert.Equal(
            "43574C310202000030000000070000000000000033221100554477668899AABBCCDDEEFF03000000FCFFFFFFF1399120010203",
            Convert.ToHexString(encoded));
    }


    [Fact]
    public void LegacyV1RecordRemainsReadable()
    {
        var encoded = Convert.FromHexString(
            "43574C310102000030000000070000000000000033221100554477668899AABBCCDDEEFF030000000100000061E0A88F010203");

        var decoded = WalRecordCodec.Decode(encoded);

        Assert.Equal(WalRecordType.Put, decoded.Type);
        Assert.Equal((ulong)7, decoded.Lsn);
        Assert.Equal(new TransactionId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")), decoded.TransactionId);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Payload.ToArray());
    }

    [Fact]
    public void V2PayloadLengthRedundancyDetectsHeaderCorruptionBeforeTailHandling()
    {
        var encoded = WalRecordCodec.Encode(new WalRecord(WalRecordType.Put, 6, TransactionId.New(), [1, 2, 3]));
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(36, 4), 100);

        Assert.Throws<WalCorruptionException>(
            () => WalRecordCodec.ReadValidatedPayloadLengthForScan(encoded.AsSpan(0, WalRecordCodec.HeaderSize)));
    }

    [Fact]
    public void RoundTripPreservesRecordAndCopiesPayload()
    {
        var payload = new byte[] { 10, 20, 30 };
        var record = new WalRecord(WalRecordType.Begin, 1, TransactionId.New(), payload);
        payload[0] = 99;

        var decoded = WalRecordCodec.Decode(WalRecordCodec.Encode(record));

        Assert.Equal(WalRecordType.Begin, decoded.Type);
        Assert.Equal((ulong)1, decoded.Lsn);
        Assert.Equal(record.TransactionId, decoded.TransactionId);
        Assert.Equal(new byte[] { 10, 20, 30 }, decoded.Payload.ToArray());
    }

    [Fact]
    public void ChecksumCorruptionIsRejected()
    {
        var encoded = WalRecordCodec.Encode(new WalRecord(WalRecordType.Commit, 3, TransactionId.New(), []));
        encoded[^1] ^= 1;

        Assert.Throws<WalCorruptionException>(() => WalRecordCodec.Decode(encoded));
    }

    [Fact]
    public void TruncatedHeaderAndPayloadAreRejected()
    {
        var encoded = WalRecordCodec.Encode(new WalRecord(WalRecordType.Put, 2, TransactionId.New(), [1, 2]));

        Assert.Throws<WalCorruptionException>(() => WalRecordCodec.Decode(encoded.AsSpan(0, WalRecordCodec.HeaderSize - 1)));
        Assert.Throws<WalCorruptionException>(() => WalRecordCodec.Decode(encoded.AsSpan(0, encoded.Length - 1)));
    }

    [Fact]
    public void UnsupportedVersionAndPayloadLimitAreRejected()
    {
        var encoded = WalRecordCodec.Encode(new WalRecord(WalRecordType.Abort, 4, TransactionId.New(), []));
        encoded[4] = 9;
        RewriteChecksum(encoded);
        Assert.Throws<WalFormatException>(() => WalRecordCodec.Decode(encoded));

        var oversized = WalRecordCodec.Encode(new WalRecord(WalRecordType.Abort, 5, TransactionId.New(), []));
        var oversizedLength = WalRecordCodec.MaxPayloadSize + 1u;
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(36, 4), oversizedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(40, 4), ~oversizedLength);
        RewriteChecksum(oversized);
        Assert.Throws<WalLimitException>(() => WalRecordCodec.Decode(oversized));
    }

    [Fact]
    public void InvalidIdentityAndFlagsFailBeforeEncoding()
    {
        Assert.Throws<ArgumentException>(() => new WalRecord(WalRecordType.Begin, 1, TransactionId.Empty, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalRecord(WalRecordType.Begin, 1, TransactionId.New(), [], flags: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalRecord(WalRecordType.Begin, 0, TransactionId.New(), []));
    }

    [Fact]
    public void RecordEnvelopeCanRepresentMaximumMutationPayload()
    {
        Assert.Equal(WalRecordCodec.MaxPayloadSize, WalMutationCodec.MaxRecordPayloadSize);
        Assert.True(
            WalRecordCodec.MaxPayloadSize
            >= WalMutationCodec.PutHeaderSize + WalMutationCodec.MaxKeySize + WalMutationCodec.MaxValueSize);
    }


    [Fact]
    public void CommitPayloadRoundTripsCommitSequence()
    {
        var payload = WalCommitCodec.Encode(
            new ChronicleDB.Core.Sequences.CommitSequence(42),
            32_768);
        Assert.Equal(WalCommitCodec.PayloadSize, payload.Length);
        var decoded = WalCommitCodec.Decode(payload);
        Assert.Equal(new ChronicleDB.Core.Sequences.CommitSequence(42), decoded.CommitSequence);
        Assert.Equal(32_768L, decoded.BaseDataLength);
        Assert.Throws<WalCorruptionException>(() => WalCommitCodec.Decode([]));
    }

    private static void RewriteChecksum(byte[] encoded)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            encoded.AsSpan(44, 4),
            Crc32C.ComputeWithZeroedRange(encoded, 44, 4));
}
