using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.PersistenceTests.Fixtures;
using ChronicleDB.Wal;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Formats;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.PersistenceTests;

public sealed class WalLogTests
{
    [Fact]
    public void AppendReopenAndReadPreserveOrderAndNextLsn()
    {
        using var directory = new StorageTestDirectory();
        var transactionId = TransactionId.New();

        using (var log = WalLog.Open(directory.Path))
        {
            Assert.Equal((ulong)1, log.NextLsn);
            Assert.Equal((ulong)1, log.Append(WalRecordType.Begin, transactionId, []).Lsn);
            Assert.Equal((ulong)2, log.Append(WalRecordType.Put, transactionId, [1, 2]).Lsn);
            Assert.Equal((ulong)3, log.Append(WalRecordType.Commit, transactionId, []).Lsn);
        }

        using var reopened = WalLog.Open(directory.Path);
        var records = reopened.ReadAll();
        Assert.Equal(3, records.Count);
        Assert.Equal(WalRecordType.Begin, records[0].Type);
        Assert.Equal(WalRecordType.Put, records[1].Type);
        Assert.Equal(new byte[] { 1, 2 }, records[1].Payload.ToArray());
        Assert.Equal((ulong)4, reopened.NextLsn);
    }


    [Fact]
    public void ExistingTruncatedWalHeaderIsRejectedInsteadOfReinitialized()
    {
        using var directory = new StorageTestDirectory();
        var path = Path.Combine(directory.Path, WalOptions.DefaultFileName);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Throws<WalCorruptionException>(() => WalLog.Open(directory.Path));
        Assert.Equal(3, new FileInfo(path).Length);
    }

    [Fact]
    public void IncompleteFinalHeaderIsTruncatedOnOpen()
    {
        using var directory = new StorageTestDirectory();
        long validLength;
        using (var log = WalLog.Open(directory.Path))
        {
            log.Append(WalRecordType.Begin, TransactionId.New(), []);
            validLength = new FileInfo(log.FilePath).Length;
        }

        File.AppendAllBytes(Path.Combine(directory.Path, WalOptions.DefaultFileName), [1, 2, 3]);

        using var reopened = WalLog.Open(directory.Path);
        Assert.Equal(validLength, new FileInfo(reopened.FilePath).Length);
        Assert.Single(reopened.ReadAll());
    }

    [Fact]
    public void IncompleteFinalPayloadIsTruncatedOnOpen()
    {
        using var directory = new StorageTestDirectory();
        long validLength;
        using (var log = WalLog.Open(directory.Path))
        {
            log.Append(WalRecordType.Begin, TransactionId.New(), []);
            validLength = new FileInfo(log.FilePath).Length;
        }

        var partial = WalRecordCodec.Encode(new WalRecord(WalRecordType.Put, 2, TransactionId.New(), [9, 8, 7]));
        using (var file = new FileStream(Path.Combine(directory.Path, WalOptions.DefaultFileName), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            file.Write(partial, 0, WalRecordCodec.HeaderSize + 1);
        }

        using var reopened = WalLog.Open(directory.Path);
        Assert.Equal(validLength, new FileInfo(reopened.FilePath).Length);
        Assert.Single(reopened.ReadAll());
    }


    [Fact]
    public void CorruptFinalV2PayloadLengthIsRejectedInsteadOfTruncated()
    {
        using var directory = new StorageTestDirectory();
        string path;
        using (var log = WalLog.Open(directory.Path))
        {
            log.Append(WalRecordType.Put, TransactionId.New(), [1, 2, 3]);
            path = log.FilePath;
        }

        var originalLength = new FileInfo(path).Length;
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(WalFileHeaderCodec.Size + 36, 4),
            100);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<WalCorruptionException>(() => WalLog.Open(directory.Path));
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    [Fact]
    public void CompleteCorruptionIsNotMistakenForAValidTail()
    {
        using var directory = new StorageTestDirectory();
        string path;
        using (var log = WalLog.Open(directory.Path))
        {
            log.Append(WalRecordType.Begin, TransactionId.New(), [0]);
            log.Append(WalRecordType.Commit, TransactionId.New(), []);
            path = log.FilePath;
        }

        var bytes = File.ReadAllBytes(path);
        bytes[WalFileHeaderCodec.Size + WalRecordCodec.HeaderSize] ^= 1;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<WalCorruptionException>(() => WalLog.Open(directory.Path));
    }

    [Fact]
    public void NonMonotonicLsnIsRejected()
    {
        using var directory = new StorageTestDirectory();
        var path = Path.Combine(directory.Path, WalOptions.DefaultFileName);
        var header = WalFileHeaderCodec.Encode(new WalFileHeader(Guid.NewGuid()));
        var first = WalRecordCodec.Encode(new WalRecord(WalRecordType.Begin, 2, TransactionId.New(), []));
        var second = WalRecordCodec.Encode(new WalRecord(WalRecordType.Commit, 1, TransactionId.New(), []));
        File.WriteAllBytes(path, header.Concat(first).Concat(second).ToArray());

        Assert.Throws<WalCorruptionException>(() => WalLog.Open(directory.Path));
    }

    [Fact]
    public void FileNameValidationPreventsPathTraversal()
    {
        using var directory = new StorageTestDirectory();

        Assert.Throws<WalFormatException>(() => WalLog.Open(directory.Path, new WalOptions { FileName = "..\\escape.wal" }));
        Assert.Throws<WalFormatException>(() => WalLog.Open(directory.Path, new WalOptions { FileName = "../escape.wal" }));
        Assert.Throws<WalFormatException>(() => WalLog.Open(directory.Path, new WalOptions { FileName = "wal.bin" }));
    }

    [Fact]
    public void DatabaseIdentityIsBoundToTheWalHeader()
    {
        using var directory = new StorageTestDirectory();
        using (WalLog.Open(directory.Path, Guid.NewGuid()))
        {
        }

        Assert.Throws<WalFormatException>(
            () => WalLog.Open(directory.Path, Guid.NewGuid()));
    }

    [Fact]
    public void MissingLsnIsRejectedAsCorruption()
    {
        using var directory = new StorageTestDirectory();
        var path = Path.Combine(directory.Path, WalOptions.DefaultFileName);
        var header = WalFileHeaderCodec.Encode(new WalFileHeader(Guid.NewGuid()));
        var first = WalRecordCodec.Encode(new WalRecord(WalRecordType.Begin, 1, TransactionId.New(), []));
        var third = WalRecordCodec.Encode(new WalRecord(WalRecordType.Begin, 3, TransactionId.New(), []));
        File.WriteAllBytes(path, header.Concat(first).Concat(third).ToArray());

        Assert.Throws<WalCorruptionException>(() => WalLog.Open(directory.Path));
    }
}
