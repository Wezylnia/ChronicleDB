namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Independent strong baseline for the A1 shadow-retention experiments.
/// It models exact retention within each history while treating every persistent
/// root, including BranchBase, as an unconditional direct observer of the
/// protected history. This corresponds to a flat/per-history exact MVCC policy:
/// precise version selection at each boundary, but no key-specific propagation
/// through branch shadowing.
///
/// The implementation intentionally does not call <see cref="RetentionInspector"/>
/// or production retention projection code so it can differential-check the
/// baseline used by the candidate experiments.
/// </summary>
public sealed class FlatExactRetentionProjectionBaseline
{
    private readonly ResearchRetentionSnapshot _snapshot;
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, ResearchCommittedVersionSnapshot[]>> _versionsByHistoryAndKey;

    public FlatExactRetentionProjectionBaseline(ResearchRetentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        _snapshot = snapshot;
        _versionsByHistoryAndKey = snapshot.Histories.ToDictionary(
            history => history.HistoryId,
            history => (IReadOnlyDictionary<string, ResearchCommittedVersionSnapshot[]>)history.Versions
                .GroupBy(version => version.KeyId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(version => version.CommitSequence).ToArray(),
                    StringComparer.Ordinal));
    }

    public FlatExactRetentionProjectionBaselineResult Analyze()
    {
        var required = new Dictionary<string, ResearchCommittedVersionSnapshot>(StringComparer.Ordinal);

        foreach (var history in _snapshot.Histories)
        {
            if (!_versionsByHistoryAndKey.TryGetValue(history.HistoryId, out var byKey))
            {
                continue;
            }

            foreach (var versions in byKey.Values)
            {
                ResearchCommittedVersionSnapshot? predecessorAtFloor = null;
                foreach (var version in versions)
                {
                    if (version.CommitSequence <= history.RetentionFloor)
                    {
                        predecessorAtFloor = version;
                    }

                    if (version.CommitSequence >= history.RetentionFloor)
                    {
                        required[version.VersionId] = version;
                    }
                }

                if (predecessorAtFloor is not null)
                {
                    required[predecessorAtFloor.VersionId] = predecessorAtFloor;
                }

                if (versions.Length > 0)
                {
                    required[versions[^1].VersionId] = versions[^1];
                }
            }
        }

        foreach (var active in _snapshot.ActiveBoundaries)
        {
            AddVisiblePredecessors(required, active.ProtectedHistoryId, active.Boundary);
        }

        foreach (var root in _snapshot.PersistentRoots)
        {
            AddVisiblePredecessors(required, root.ProtectedHistoryId, root.Boundary);
        }

        var orderedIds = required.Keys.Order(StringComparer.Ordinal).ToArray();
        return new FlatExactRetentionProjectionBaselineResult(
            RequiredVersionIds: Array.AsReadOnly(orderedIds),
            RetainedVersionCount: required.Count,
            RetainedPayloadBytes: checked(required.Values.Sum(version => version.LogicalPayloadBytes)),
            RetainedSerializedBytes: checked(required.Values.Sum(version => version.LogicalSerializedBytes)));
    }

    private void AddVisiblePredecessors(
        Dictionary<string, ResearchCommittedVersionSnapshot> required,
        Guid historyId,
        ulong boundary)
    {
        if (!_versionsByHistoryAndKey.TryGetValue(historyId, out var byKey))
        {
            return;
        }

        foreach (var versions in byKey.Values)
        {
            ResearchCommittedVersionSnapshot? visible = null;
            foreach (var version in versions)
            {
                if (version.CommitSequence > boundary)
                {
                    break;
                }

                visible = version;
            }

            if (visible is not null)
            {
                required[visible.VersionId] = visible;
            }
        }
    }

    private static void Validate(ResearchRetentionSnapshot snapshot)
    {
        if (snapshot.Histories.GroupBy(history => history.HistoryId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("History IDs must be unique.", nameof(snapshot));
        }

        var historyIds = snapshot.Histories.Select(history => history.HistoryId).ToHashSet();
        if (snapshot.PersistentRoots.Any(root => !historyIds.Contains(root.ProtectedHistoryId))
            || snapshot.ActiveBoundaries.Any(boundary => !historyIds.Contains(boundary.ProtectedHistoryId)))
        {
            throw new ArgumentException("Every observer boundary must reference a captured history.", nameof(snapshot));
        }

        foreach (var history in snapshot.Histories)
        {
            if (history.RetentionFloor > history.CurrentSequence)
            {
                throw new ArgumentException("A history retention floor cannot exceed its current sequence.", nameof(snapshot));
            }

            if (history.Versions.Any(version => version.CommitSequence > history.CurrentSequence))
            {
                throw new ArgumentException("A captured version cannot exceed its history current sequence.", nameof(snapshot));
            }
        }
    }
}

public sealed record FlatExactRetentionProjectionBaselineResult(
    IReadOnlyList<string> RequiredVersionIds,
    int RetainedVersionCount,
    long RetainedPayloadBytes,
    long RetainedSerializedBytes);
