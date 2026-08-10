using System.Buffers.Binary;
using ChronicleDB;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Storage.Formats;
using ChronicleDB.Storage.Pages;

namespace ChronicleDB.PersistenceTests;

public sealed class StorageFormatTests
{
    [Fact]
    public void Crc32CKnownVectorMatchesTheStandardCheckValue()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
    }

    [Fact]
    public void HeaderRoundTripPreservesIdentityAndConfiguration()
    {
        var expected = new DatabaseHeader(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            StorageOptions.DefaultPageSize,
            FormatFlags: 0,
            CreatedUnixMilliseconds: 1_700_000_000_000);

        var bytes = DatabaseHeaderCodec.Encode(expected);
        var actual = DatabaseHeaderCodec.Decode(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(DatabaseHeaderCodec.Size, bytes.Length);
        Assert.Equal(
            "4348444276303031010000004000000033221100554477668899AABBCCDDEEFF0040000001000000000000000068E5CF8B0100000000000000000000624A1567",
            Convert.ToHexString(bytes));
    }

    [Fact]
    public void HeaderChecksumDetectsSingleByteCorruption()
    {
        var bytes = DatabaseHeaderCodec.Encode(new DatabaseHeader(
            Guid.NewGuid(),
            StorageOptions.DefaultPageSize,
            FormatFlags: 0,
            CreatedUnixMilliseconds: 1));
        bytes[44] ^= 0x01;

        Assert.Throws<StorageCorruptionException>(() => DatabaseHeaderCodec.Decode(bytes));
    }

    [Fact]
    public void HeaderRejectsIncompatibleMajorVersionAfterChecksumValidation()
    {
        var bytes = DatabaseHeaderCodec.Encode(new DatabaseHeader(
            Guid.NewGuid(),
            StorageOptions.DefaultPageSize,
            FormatFlags: 0,
            CreatedUnixMilliseconds: 1));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 99);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), Crc32C.Compute(bytes.AsSpan(0, 60)));

        Assert.Throws<StorageFormatException>(() => DatabaseHeaderCodec.Decode(bytes));
    }

    [Fact]
    public void PageRoundTripPreservesPayloadAndHeader()
    {
        var expectedHeader = new PageHeader(
            new ChronicleDB.Core.Identifiers.PageId(7),
            PageType.Record,
            Generation: 1,
            PayloadLength: 3);
        var bytes = PageCodec.Encode(expectedHeader, [1, 2, 3], StorageOptions.DefaultPageSize);

        var decoded = PageCodec.Decode(bytes, StorageOptions.DefaultPageSize);

        Assert.Equal(expectedHeader, decoded.Header);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Payload);
    }

    [Fact]
    public void PageChecksumDetectsPayloadCorruption()
    {
        var bytes = PageCodec.Encode(
            new PageHeader(new ChronicleDB.Core.Identifiers.PageId(1), PageType.Record, 1, 1),
            [0xAA],
            StorageOptions.DefaultPageSize);
        bytes[PageCodec.Size] ^= 0x01;

        Assert.Throws<StorageCorruptionException>(
            () => PageCodec.Decode(bytes, StorageOptions.DefaultPageSize));
    }

    [Fact]
    public void PageRejectsNonZeroPadding()
    {
        var bytes = PageCodec.Encode(
            new PageHeader(new ChronicleDB.Core.Identifiers.PageId(1), PageType.Record, 1, 1),
            [0xAA],
            StorageOptions.DefaultPageSize);
        bytes[^1] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), Crc32C.ComputeWithZeroedRange(bytes, 28, 4));

        Assert.Throws<StorageCorruptionException>(
            () => PageCodec.Decode(bytes, StorageOptions.DefaultPageSize));
    }

    [Fact]
    public void TruncatedDataFileIsRejectedOnOpen()
    {
        using var directory = new StorageTestDirectory();
        using (ChronicleDatabase.Open(directory.Path))
        {
        }

        var dataPath = System.IO.Path.Combine(directory.Path, "chronicle.data");
        File.WriteAllBytes(dataPath, [1]);

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void IncompleteFinalDataPageIsReplayedFromDurableWal()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([7], [8]);
        }

        var dataPath = System.IO.Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        File.AppendAllBytes(dataPath, [0xCC]);

        using var reopened = ChronicleDatabase.Open(directory.Path);
        Assert.True(reopened.TryGet([7], out var value));
        Assert.Equal(new byte[] { 8 }, value);
    }

    [Fact]
    public void CorruptRecordPageIsRejectedOnOpen()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], [2, 3, 4]);
        }

        var dataPath = System.IO.Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        var bytes = File.ReadAllBytes(dataPath);
        bytes[PageCodec.Size + 1] ^= 0x01;
        File.WriteAllBytes(dataPath, bytes);

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void CyclicOverflowChainIsRejectedOnOpen()
    {
        using var directory = new StorageTestDirectory();
        using (var database = ChronicleDatabase.Open(directory.Path))
        {
            database.Put([1], Enumerable.Repeat((byte)7, 40_000).ToArray());
        }

        var dataPath = System.IO.Path.Combine(directory.Path, PersistentKeyValueStore.DataFileName);
        var bytes = File.ReadAllBytes(dataPath);
        var pageSize = StorageOptions.DefaultPageSize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32, 8), 1);
        var firstPage = bytes.AsSpan(0, pageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            firstPage[28..32],
            Crc32C.ComputeWithZeroedRange(firstPage, 28, 4));
        File.WriteAllBytes(dataPath, bytes);

        Assert.Throws<StorageCorruptionException>(
            () => ChronicleDatabase.Open(directory.Path).Dispose());
    }

    [Fact]
    public void InvalidStorageLimitsAreRejectedBeforeOpeningFiles()
    {
        using var directory = new StorageTestDirectory();

        Assert.Throws<StorageLimitException>(
            () => ChronicleDatabase.Open(directory.Path, new StorageOptions { MaxKeySize = 0 }));
        Assert.Throws<StorageLimitException>(
            () => ChronicleDatabase.Open(directory.Path, new StorageOptions { MaxValueSize = 0 }));
    }
}
