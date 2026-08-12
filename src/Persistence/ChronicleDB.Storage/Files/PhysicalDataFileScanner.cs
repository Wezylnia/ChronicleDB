using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Storage.Pages;
using ChronicleDB.Storage.Records;

namespace ChronicleDB.Storage.Files;

/// <summary>
/// Research-only structural scanner for every record page that is still physically
/// present in an append-oriented ChronicleDB data generation. It intentionally does
/// not consult the live key index, so superseded and tombstoned records remain visible.
/// </summary>
internal static class PhysicalDataFileScanner
{
    internal static PhysicalDataFileScanResult Scan(Stream stream, string sourceName, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(options);

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Physical data scanning requires a readable seekable stream.", nameof(stream));
        }

        var issues = new List<string>();
        var records = new List<PhysicalDataRecordSnapshot>();
        var fullPageCount = stream.Length / options.PageSize;
        if (stream.Length % options.PageSize != 0)
        {
            issues.Add($"{sourceName}: data length {stream.Length} is not page aligned.");
        }

        var pages = new Dictionary<PageId, DecodedPage>();
        var originalPosition = stream.Position;
        try
        {
            var pageBytes = new byte[options.PageSize];
            for (long zeroBased = 0; zeroBased < fullPageCount; zeroBased++)
            {
                var expectedPageId = new PageId(checked((ulong)zeroBased + 1));
                try
                {
                    ReadExactly(stream, pageBytes, checked(zeroBased * options.PageSize));
                    var decoded = PageCodec.Decode(pageBytes, options.PageSize);
                    if (decoded.Header.PageId != expectedPageId)
                    {
                        throw new StorageCorruptionException(
                            $"Physical page {expectedPageId.Value} declares page ID {decoded.Header.PageId.Value}.");
                    }

                    pages.Add(expectedPageId, decoded);
                }
                catch (Exception exception) when (exception is StorageException or IOException or EndOfStreamException)
                {
                    issues.Add($"{sourceName}: page {expectedPageId.Value} could not be decoded: {exception.Message}");
                }
            }

            foreach (var pair in pages.OrderBy(pair => pair.Key.Value))
            {
                if (pair.Value.Header.Type != PageType.Record)
                {
                    continue;
                }

                try
                {
                    var decodedRecord = RecordCodec.Decode(pair.Value.Payload, options);
                    var overflowPages = new List<PageId>();
                    var value = decodedRecord.IsTombstone
                        ? []
                        : ReadValue(decodedRecord, pages, options.PageSize, overflowPages);
                    records.Add(new PhysicalDataRecordSnapshot(
                        pair.Key,
                        checked(((long)pair.Key.Value - 1) * options.PageSize),
                        decodedRecord.Key,
                        decodedRecord.IsTombstone,
                        value,
                        Array.AsReadOnly(overflowPages.ToArray())));
                }
                catch (Exception exception) when (exception is StorageException or OverflowException)
                {
                    issues.Add($"{sourceName}: record page {pair.Key.Value} could not be decoded completely: {exception.Message}");
                }
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }

        return new PhysicalDataFileScanResult(
            sourceName,
            stream.Length,
            Array.AsReadOnly(records.ToArray()),
            issues.Count == 0,
            Array.AsReadOnly(issues.ToArray()));
    }

    private static byte[] ReadValue(
        DecodedRecord record,
        Dictionary<PageId, DecodedPage> pages,
        int pageSize,
        List<PageId> overflowPages)
    {
        if (!record.OverflowHead.IsValid)
        {
            return record.InlineValue;
        }

        var result = new byte[record.ValueLength];
        var visited = new HashSet<PageId>();
        var current = record.OverflowHead;
        var offset = 0;
        while (current.IsValid)
        {
            if (!visited.Add(current) || !pages.TryGetValue(current, out var decoded))
            {
                throw new StorageCorruptionException("Physical overflow chain is cyclic or references an undecodable page.");
            }

            if (decoded.Header.Type != PageType.Overflow)
            {
                throw new StorageCorruptionException("Physical record overflow chain references a non-overflow page.");
            }

            var overflow = OverflowCodec.Decode(decoded.Payload, pageSize);
            if (overflow.Chunk.Length > result.Length - offset)
            {
                throw new StorageCorruptionException("Physical overflow chain exceeds its declared value length.");
            }

            overflow.Chunk.CopyTo(result.AsSpan(offset));
            overflowPages.Add(current);
            offset += overflow.Chunk.Length;
            if (overflow.NextPage.IsValid && overflow.NextPage.Value <= current.Value)
            {
                throw new StorageCorruptionException("Physical overflow chain does not advance monotonically.");
            }

            current = overflow.NextPage;
        }

        if (offset != result.Length)
        {
            throw new StorageCorruptionException("Physical overflow chain is shorter than its declared value length.");
        }

        return result;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, long offset)
    {
        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                throw new EndOfStreamException("Physical data page ended before the configured page boundary.");
            }
            total += read;
        }
    }
}

internal sealed record PhysicalDataRecordSnapshot(
    PageId RecordPageId,
    long ByteOffset,
    BinaryKey PhysicalKey,
    bool IsStorageTombstone,
    byte[] Value,
    IReadOnlyList<PageId> OverflowPages);

internal sealed record PhysicalDataFileScanResult(
    string SourceName,
    long FileLength,
    IReadOnlyList<PhysicalDataRecordSnapshot> Records,
    bool IsComplete,
    IReadOnlyList<string> Issues);
