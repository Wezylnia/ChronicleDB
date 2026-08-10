using ChronicleDB.Core.Identifiers;
using ChronicleDB.History.Branches;

namespace ChronicleDB;

/// <summary>
/// Writable handle to one independently evolving v0.7 history domain.
/// Parent state is fixed at branch creation and never follows later parent commits.
/// </summary>
public sealed class ChronicleBranch : IDisposable
{
    public const int MaximumDepth = BranchCatalog.MaximumDepth;

    private readonly ChronicleDatabase _database;
    private int _disposed;

    internal ChronicleBranch(ChronicleDatabase database, ChronicleBranchInfo info)
    {
        _database = database;
        Info = info;
    }

    public ChronicleBranchInfo Info { get; private set; }

    public Guid BranchId => Info.BranchId;

    public Guid HistoryId => Info.HistoryId;

    public string Name => Info.Name;

    public ulong CurrentSequence
    {
        get
        {
            ThrowIfDisposed();
            Info = _database.GetBranchInfo(new BranchId(BranchId));
            return Info.CurrentSequence;
        }
    }

    public ChronicleTransaction BeginTransaction()
    {
        ThrowIfDisposed();
        return _database.BeginBranchTransaction(new BranchId(BranchId));
    }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        return _database.ReadBranchCurrent(new BranchId(BranchId), key, out value);
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        using var transaction = BeginTransaction();
        transaction.Put(key, value);
        transaction.Commit();
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        using var transaction = BeginTransaction();
        var existed = transaction.TryGet(key, out _);
        transaction.Delete(key);
        transaction.Commit();
        return existed;
    }

    public ChronicleBranchSnapshot CreateSnapshot(string name)
    {
        ThrowIfDisposed();
        return _database.CreateBranchSnapshot(new BranchId(BranchId), name);
    }

    public IReadOnlyList<ChronicleBranchSnapshotInfo> ListSnapshots()
    {
        ThrowIfDisposed();
        return _database.ListBranchSnapshots(new BranchId(BranchId));
    }

    public ChronicleBranchSnapshot OpenSnapshot(Guid snapshotId)
    {
        ThrowIfDisposed();
        return _database.OpenBranchSnapshot(new BranchId(BranchId), snapshotId);
    }

    public ChronicleBranchHistoricalView OpenHistoricalView(ulong sequence)
    {
        ThrowIfDisposed();
        return _database.OpenBranchHistoricalView(new BranchId(BranchId), sequence);
    }

    public ChronicleBranch CreateBranch(string name)
    {
        ThrowIfDisposed();
        return _database.CreateBranchFromBranch(new BranchId(BranchId), CurrentSequence, name);
    }

    public ChronicleBranch CreateBranch(string name, ulong sequence)
    {
        ThrowIfDisposed();
        return _database.CreateBranchFromBranch(new BranchId(BranchId), sequence, name);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
