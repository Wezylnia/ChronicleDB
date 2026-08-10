using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

public sealed class ChronicleBranchHistoricalView : IDisposable
{
    private readonly ChronicleDatabase _database;
    private readonly BranchId _branchId;
    private int _disposed;

    internal ChronicleBranchHistoricalView(ChronicleDatabase database, BranchId branchId, ulong sequence)
    {
        _database = database;
        _branchId = branchId;
        Sequence = sequence;
    }

    public Guid BranchId => _branchId.Value;

    public ulong Sequence { get; }

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        ThrowIfDisposed();
        return _database.ReadBranchHistorical(_branchId, key, new CommitSequence(Sequence), out value);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
