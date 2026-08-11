using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

public sealed class ChronicleBranchSnapshot : IDisposable
{
    private readonly ChronicleDatabase _database;
    private readonly long _boundaryToken;
    private int _disposed;

    internal ChronicleBranchSnapshot(
        ChronicleDatabase database,
        ChronicleBranchSnapshotInfo info,
        long boundaryToken)
    {
        _database = database;
        _boundaryToken = boundaryToken;
        Info = info;
    }

    public ChronicleBranchSnapshotInfo Info { get; }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        return _database.ReadBranchHistorical(
            new BranchId(Info.BranchId),
            key,
            new CommitSequence(Info.Sequence),
            out value);
    }

    public ChronicleBranch CreateBranch(string name)
    {
        ThrowIfDisposed();
        return _database.CreateBranchFromPinnedBranchBoundary(
            new BranchId(Info.BranchId),
            new CommitSequence(Info.Sequence),
            name);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _database.BranchHistoricalHandleClosed(new BranchId(Info.BranchId), _boundaryToken);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
