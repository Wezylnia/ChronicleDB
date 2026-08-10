using System.Diagnostics;
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
    private long _bytesWritten;
    private long _flushCount;
    private long _flushElapsedTicks;
    private bool _faulted;
    private bool _disposed;

    private WalLog(FileStream stream, WalOptions options, ulong nextLsn)
    {
        _stream = stream;
        _options = options;
        _nextLsn = nextLsn;
    }

    public string FilePath => _stream.Name;

    public long FileLength
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _stream.Length;
            }
        }
    }

    public ulong NextLsn
    {
        get
        {
            lock (_gate)
            {
                ThrowIfUsable();
                return _nextLsn;
            }
        }
    }

    public static WalLog Open(string directory, WalOptions? options = null)
        => Open(directory, Guid.Empty, options);

    public static WalLog Open(string directory, Guid expectedDatabaseId, WalOptions? options = null)
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
            EnsureCreatedAtomically(path, expectedDatabaseId);
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            var header = EnsureHeader(stream, expectedDatabaseId);
            if (expectedDatabaseId != Guid.Empty && header.DatabaseId != expectedDatabaseId)
            {
                throw new WalFormatException("WAL database identity does not match the storage database.");
            }

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
            ThrowIfUsable();
            if (_nextLsn == ulong.MaxValue)
            {
                throw new WalLimitException("WAL LSN space is exhausted.");
            }

            var record = new WalRecord(type, _nextLsn, transactionId, payload);
            var encoded = WalRecordCodec.Encode(record);

            try
            {
                _stream.Position = _stream.Length;
                _stream.Write(encoded);
                if (_options.FlushOnAppend)
                {
                    FlushCore();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _faulted = true;
                throw new WalException("WAL append failed and the log must be reopened before reuse.", exception);
            }

            _nextLsn++;
            Interlocked.Add(ref _bytesWritten, encoded.Length);
            return record;
        }
    }

    public IReadOnlyList<WalRecord> ReadAll()
    {
        lock (_gate)
        {
            ThrowIfUsable();
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
            ThrowIfUsable();
            FlushCore();
        }
    }

    internal void MarkFaultedAfterUncertainWrite()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _faulted = true;
        }
    }

    public WalStatistics GetStatistics()
    {
        lock (_gate)
        {
            ThrowIfUsable();
            return new WalStatistics(
                FileLength: _stream.Length,
                BytesWrittenThisSession: Volatile.Read(ref _bytesWritten),
                FlushCount: Volatile.Read(ref _flushCount),
                TotalFlushStopwatchTicks: Volatile.Read(ref _flushElapsedTicks),
                NextLsn: _nextLsn);
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
                if (!_faulted)
                {
                    _stream.Flush(flushToDisk: true);
                }
            }
            finally
            {
                _stream.Dispose();
                _disposed = true;
            }
        }
    }

    private static WalFileHeader EnsureHeader(FileStream stream, Guid expectedDatabaseId)
    {
        if (stream.Length < WalFileHeaderCodec.Size)
        {
            throw new WalCorruptionException("WAL file header is truncated.");
        }

        var bytes = new byte[WalFileHeaderCodec.Size];
        ReadExactly(stream, bytes, 0);
        return WalFileHeaderCodec.Decode(bytes);
    }


    private static void EnsureCreatedAtomically(string path, Guid expectedDatabaseId)
    {
        if (File.Exists(path))
        {
            return;
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".creating";
        var databaseId = expectedDatabaseId == Guid.Empty ? Guid.NewGuid() : expectedDatabaseId;
        try
        {
            using (var temporary = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4 * 1024,
                       options: FileOptions.WriteThrough))
            {
                temporary.Write(WalFileHeaderCodec.Encode(new WalFileHeader(databaseId)));
                temporary.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another opener won creation. The exclusive open below will determine
                // whether this process may proceed and header identity validation follows.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static WalScan Scan(FileStream stream)
    {
        var records = new List<WalRecord>();
        var position = (long)WalFileHeaderCodec.Size;
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
            var payloadLength = WalRecordCodec.ReadValidatedPayloadLengthForScan(header);
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
            var expectedLsn = NextLsnAfter(lastLsn);
            if (record.Lsn != expectedLsn)
            {
                throw new WalCorruptionException(
                    $"WAL LSN sequence is discontinuous: expected {expectedLsn}, found {record.Lsn}.");
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

    private void FlushCore()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            _stream.Flush(flushToDisk: true);
            Interlocked.Increment(ref _flushCount);
            Interlocked.Add(ref _flushElapsedTicks, Stopwatch.GetTimestamp() - started);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _faulted = true;
            throw new WalException("WAL flush failed and the log must be reopened before reuse.", exception);
        }
    }

    private void ThrowIfUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new WalException("The WAL is faulted after an uncertain I/O operation and must be reopened.");
        }
    }

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

public readonly record struct WalStatistics(
    long FileLength,
    long BytesWrittenThisSession,
    long FlushCount,
    long TotalFlushStopwatchTicks,
    ulong NextLsn);
