using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

/// <summary>
/// Read-only handle pinned to the immutable visibility boundary of a persistent snapshot.
/// Deleting the named snapshot does not invalidate an already-open handle in v0.5 because
/// historical reclamation is deliberately conservative.
/// </summary>
public sealed class ChronicleSnapshot : IDisposable
{
    private readonly ChronicleDatabase _database;
    private int _disposed;

    internal ChronicleSnapshot(ChronicleDatabase database, ChronicleSnapshotInfo info)
    {
        _database = database;
        Info = info;
    }

    public ChronicleSnapshotInfo Info { get; }

    public Guid SnapshotId => Info.SnapshotId;

    public string Name => Info.Name;

    public ulong Sequence => Info.Sequence;

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        return _database.ReadHistorical(key, new CommitSequence(Info.Sequence), out value);
    }

    public ChronicleBranch CreateBranch(string name)
    {
        ThrowIfDisposed();
        return _database.CreateBranchFromSnapshot(SnapshotId, name);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
