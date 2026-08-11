namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Research-only coarse retention baseline that models a conventional per-history
/// oldest-boundary horizon. Unlike root-exact accounting, a protected old boundary
/// keeps every later version in that history, even when no observer needs the
/// intermediate versions. This class is observational and never participates in GC.
/// </summary>
public static class CoarseOldestRootRetentionAnalyzer
{
    public static CoarseOldestRootRetentionResult Analyze(ResearchRetentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var withRoots = Collect(snapshot, includePersistentRoots: true);
        var withoutRoots = Collect(snapshot, includePersistentRoots: false);
        var rootInducedIds = withRoots.Keys.Except(withoutRoots.Keys, StringComparer.Ordinal).ToArray();

        return new CoarseOldestRootRetentionResult(
            VersionCountWithPersistentRoots: withRoots.Count,
            VersionCountWithoutPersistentRoots: withoutRoots.Count,
            RootInducedVersionCount: rootInducedIds.Length,
            PayloadBytesWithPersistentRoots: Sum(withRoots.Values, version => version.LogicalPayloadBytes),
            PayloadBytesWithoutPersistentRoots: Sum(withoutRoots.Values, version => version.LogicalPayloadBytes),
            RootInducedPayloadBytes: Sum(rootInducedIds.Select(id => withRoots[id]), version => version.LogicalPayloadBytes),
            SerializedBytesWithPersistentRoots: Sum(withRoots.Values, version => version.LogicalSerializedBytes),
            SerializedBytesWithoutPersistentRoots: Sum(withoutRoots.Values, version => version.LogicalSerializedBytes),
            RootInducedSerializedBytes: Sum(rootInducedIds.Select(id => withRoots[id]), version => version.LogicalSerializedBytes));
    }

    private static Dictionary<string, ResearchCommittedVersionSnapshot> Collect(
        ResearchRetentionSnapshot snapshot,
        bool includePersistentRoots)
    {
        var retained = new Dictionary<string, ResearchCommittedVersionSnapshot>(StringComparer.Ordinal);
        foreach (var history in snapshot.Histories)
        {
            var effectiveBoundary = history.RetentionFloor;
            foreach (var active in snapshot.ActiveBoundaries.Where(boundary => boundary.ProtectedHistoryId == history.HistoryId))
            {
                effectiveBoundary = Math.Min(effectiveBoundary, active.Boundary);
            }

            if (includePersistentRoots)
            {
                foreach (var root in snapshot.PersistentRoots.Where(root => root.ProtectedHistoryId == history.HistoryId))
                {
                    effectiveBoundary = Math.Min(effectiveBoundary, root.Boundary);
                }
            }

            foreach (var version in history.Versions.Where(version => version.CommitSequence >= effectiveBoundary))
            {
                Add(retained, history.HistoryId, version);
            }

            foreach (var predecessor in history.Versions
                         .Where(version => version.CommitSequence <= effectiveBoundary)
                         .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                         .Select(group => group.OrderBy(version => version.CommitSequence).Last()))
            {
                Add(retained, history.HistoryId, predecessor);
            }

            foreach (var newest in history.Versions
                         .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                         .Select(group => group.OrderBy(version => version.CommitSequence).Last()))
            {
                Add(retained, history.HistoryId, newest);
            }
        }

        return retained;
    }

    private static void Add(
        Dictionary<string, ResearchCommittedVersionSnapshot> target,
        Guid historyId,
        ResearchCommittedVersionSnapshot version)
    {
        var identity = $"{historyId:N}/{version.VersionId}";
        target[identity] = version;
    }

    private static long Sum(
        IEnumerable<ResearchCommittedVersionSnapshot> versions,
        Func<ResearchCommittedVersionSnapshot, long> selector)
        => checked(versions.Sum(selector));
}

public sealed record CoarseOldestRootRetentionResult(
    int VersionCountWithPersistentRoots,
    int VersionCountWithoutPersistentRoots,
    int RootInducedVersionCount,
    long PayloadBytesWithPersistentRoots,
    long PayloadBytesWithoutPersistentRoots,
    long RootInducedPayloadBytes,
    long SerializedBytesWithPersistentRoots,
    long SerializedBytesWithoutPersistentRoots,
    long RootInducedSerializedBytes);
