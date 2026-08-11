using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

/// <summary>
/// Read-only handle pinned to the immutable visibility boundary of a persistent snapshot.
/// Deleting the named snapshot does not invalidate an already-open handle because the
/// handle itself participates in process-local history retention until disposed.
/// </summary>
public sealed class ChronicleSnapshot : IDisposable
{
    private readonly ChronicleDatabase _database;
    private readonly long _boundaryToken;
    private int _disposed;

    internal ChronicleSnapshot(ChronicleDatabase database, ChronicleSnapshotInfo info, long boundaryToken)
    {
        _database = database;
        _boundaryToken = boundaryToken;
        Info = info;
    }

    public ChronicleSnapshotInfo Info { get; }

    public Guid SnapshotId => Info.SnapshotId;

    public string Name => Info.Name;

    public ulong Sequence => Info.Sequence;

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        return _database.ReadPinnedHistorical(key, new CommitSequence(Info.Sequence), out value);
    }

    public ChronicleBranch CreateBranch(string name)
    {
        ThrowIfDisposed();
        return _database.CreateBranchFromPinnedMainBoundary(new CommitSequence(Info.Sequence), name);
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
