using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

public sealed class ChronicleBranchSnapshot : IDisposable
{
    private readonly ChronicleDatabase _database;
    private int _disposed;

    internal ChronicleBranchSnapshot(ChronicleDatabase database, ChronicleBranchSnapshotInfo info)
    {
        _database = database;
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
        return _database.CreateBranchFromBranch(new BranchId(Info.BranchId), Info.Sequence, name);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
