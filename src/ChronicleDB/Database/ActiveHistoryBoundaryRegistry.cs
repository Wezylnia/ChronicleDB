using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB;

/// <summary>
/// Process-local retention boundaries owned by open readers and transactions.
/// Persistent roots protect durable observers; this registry protects observers
/// whose lifetime is represented only by an open managed handle.
/// </summary>
internal sealed class ActiveHistoryBoundaryRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<long, ActiveHistoryBoundary> _boundaries = [];
    private long _nextToken;

    public long Register(HistoryId historyId, CommitSequence boundary)
    {
        if (!historyId.IsValid)
        {
            throw new ArgumentException("History identity must be valid.", nameof(historyId));
        }

        lock (_gate)
        {
            if (_nextToken == long.MaxValue)
            {
                throw new InvalidOperationException("Active historical-handle token space is exhausted.");
            }

            var token = ++_nextToken;
            _boundaries.Add(token, new ActiveHistoryBoundary(historyId, boundary));
            return token;
        }
    }

    public void Release(long token)
    {
        if (token <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _boundaries.Remove(token);
        }
    }

    public IReadOnlyList<CommitSequence> ListBoundaries(HistoryId historyId)
    {
        lock (_gate)
        {
            return _boundaries.Values
                .Where(item => item.HistoryId == historyId)
                .Select(item => item.Boundary)
                .Distinct()
                .OrderBy(item => item.Value)
                .ToArray();
        }
    }

    public int CountForHistory(HistoryId historyId)
    {
        if (!historyId.IsValid)
        {
            return 0;
        }

        lock (_gate)
        {
            return _boundaries.Values.Count(item => item.HistoryId == historyId);
        }
    }

    public bool Contains(HistoryId historyId, CommitSequence boundary)
    {
        lock (_gate)
        {
            return _boundaries.Values.Any(item => item.HistoryId == historyId && item.Boundary == boundary);
        }
    }

    private readonly record struct ActiveHistoryBoundary(HistoryId HistoryId, CommitSequence Boundary);
}
