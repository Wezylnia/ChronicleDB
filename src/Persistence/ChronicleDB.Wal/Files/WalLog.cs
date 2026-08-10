using System.Buffers.Binary;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Formats;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.Wal.Files;

/// <summary>
/// Exclusive append-only WAL with deterministic incomplete-tail handling.
/// </summary>
public sealed class WalLog : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly WalOptions _options;
    private ulong _nextLsn;
    private bool _disposed;

    private WalLog(FileStream stream, WalOptions options, ulong nextLsn)
    {
        _stream = stream;
        _options = options;
        _nextLsn = nextLsn;
    }

    public string FilePath => _stream.Name;

    public ulong NextLsn
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _nextLsn;
            }
        }
    }

    public static WalLog Open(string directory, WalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var validatedOptions = options ?? new WalOptions();
        validatedOptions.Validate();

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, validatedOptions.FileName);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            var scan = Scan(stream);
            if (scan.TailBytes != 0)
            {
                stream.SetLength(scan.ValidLength);
                stream.Flush(flushToDisk: true);
            }

            return new WalLog(stream, validatedOptions, scan.NextLsn);
        }
        catch (IOException exception)
        {
            stream?.Dispose();
            throw new WalException($"Could not open WAL '{path}'.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public WalRecord Append(
        WalRecordType type,
        TransactionId transactionId,
        ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_nextLsn == ulong.MaxValue)
            {
                throw new WalLimitException("WAL LSN space is exhausted.");
            }

            var record = new WalRecord(type, _nextLsn, transactionId, payload);
            var encoded = WalRecordCodec.Encode(record);

            _stream.Position = _stream.Length;
            _stream.Write(encoded);
            if (_options.FlushOnAppend)
            {
                _stream.Flush(flushToDisk: true);
            }

            _nextLsn++;
            return record;
        }
    }

    public IReadOnlyList<WalRecord> ReadAll()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var scan = Scan(_stream);
            if (scan.TailBytes != 0)
            {
                throw new WalCorruptionException("WAL contains an incomplete tail after opening.");
            }

            return scan.Records;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                _stream.Dispose();
                _disposed = true;
            }
        }
    }

    private static WalScan Scan(FileStream stream)
    {
        var records = new List<WalRecord>();
        var position = 0L;
        var lastLsn = 0UL;
        var length = stream.Length;
        var header = new byte[WalRecordCodec.HeaderSize];

        while (position < length)
        {
            var remaining = length - position;
            if (remaining < WalRecordCodec.HeaderSize)
            {
                return new WalScan(records, position, remaining, NextLsnAfter(lastLsn));
            }

            ReadExactly(stream, header, position);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
            if (payloadLength > WalRecordCodec.MaxPayloadSize)
            {
                throw new WalLimitException("WAL payload exceeds the maximum supported size.");
            }

            var recordLength = checked(WalRecordCodec.HeaderSize + (long)payloadLength);
            if (recordLength > remaining)
            {
                return new WalScan(records, position, remaining, NextLsnAfter(lastLsn));
            }

            var encoded = new byte[checked((int)recordLength)];
            header.CopyTo(encoded, 0);
            if (payloadLength != 0)
            {
                ReadExactly(stream, encoded.AsSpan(WalRecordCodec.HeaderSize), position + WalRecordCodec.HeaderSize);
            }

            var record = WalRecordCodec.Decode(encoded);
            if (record.Lsn <= lastLsn)
            {
                throw new WalCorruptionException("WAL LSNs must be strictly increasing.");
            }

            records.Add(record);
            lastLsn = record.Lsn;
            position += recordLength;
        }

        return new WalScan(records, position, 0, NextLsnAfter(lastLsn));
    }

    private static void ReadExactly(Stream stream, byte[] buffer, long offset)
    {
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                throw new WalCorruptionException("WAL ended before the requested bytes were read.");
            }

            read += count;
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, long offset)
    {
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                throw new WalCorruptionException("WAL ended before the requested bytes were read.");
            }

            read += count;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static ulong NextLsnAfter(ulong lastLsn)
    {
        if (lastLsn == ulong.MaxValue)
        {
            throw new WalLimitException("WAL LSN space is exhausted.");
        }

        return lastLsn + 1;
    }

    private sealed record WalScan(
        IReadOnlyList<WalRecord> Records,
        long ValidLength,
        long TailBytes,
        ulong NextLsn);
}
