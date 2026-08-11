using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

/// <summary>
/// Read-only point-in-time view over a retained commit-sequence boundary.
/// </summary>
public sealed class ChronicleHistoricalView : IDisposable
{
    private readonly ChronicleDatabase _database;
    private readonly long _boundaryToken;
    private int _disposed;

    internal ChronicleHistoricalView(ChronicleDatabase database, ulong sequence, long boundaryToken)
    {
        _database = database;
        _boundaryToken = boundaryToken;
        Sequence = sequence;
    }

    public Guid DatabaseId => _database.DatabaseId;

    public ulong Sequence { get; }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        return _database.ReadPinnedHistorical(key, new CommitSequence(Sequence), out value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _database.HistoricalHandleClosed(_boundaryToken);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
