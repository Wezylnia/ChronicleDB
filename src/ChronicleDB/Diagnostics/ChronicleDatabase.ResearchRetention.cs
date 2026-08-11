using System.Security.Cryptography;
using ChronicleDB.Core.Keys;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.Transactions.Mvcc;

namespace ChronicleDB;

public sealed partial class ChronicleDatabase
{
    /// <summary>
    /// Captures raw logical MVCC/retention facts for independent research analysis.
    /// This method is observational only: the returned data is never used by the
    /// production retention or reclamation path.
    /// </summary>
    public ResearchRetentionSnapshot CaptureResearchRetentionSnapshot()
    {
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var histories = new List<ResearchHistoryRetentionSnapshot>(_branchRuntimes.Count + 1)
                {
                    CaptureHistory(
                        _mainHistoryId.Value,
                        GetHistoryRetentionFloor(),
                        GetCurrentCommitSequence(),
                        _versions.SnapshotHistory()),
                };

                histories.AddRange(_branchRuntimes.Values
                    .OrderBy(runtime => runtime.Definition.Depth)
                    .ThenBy(runtime => runtime.Definition.HistoryId.Value)
                    .Select(runtime => CaptureHistory(
                        runtime.Definition.HistoryId.Value,
                        runtime.HistoryFloor,
                        runtime.Definition.LocalCurrentSequence,
                        runtime.Versions.SnapshotHistory())));

                var roots = _historyRoots.ListActive()
                    .OrderBy(root => root.RootId.Value)
                    .Select(root => new ResearchPersistentRetentionRootSnapshot(
                        root.RootId.Value,
                        root.Kind.ToString(),
                        root.HistoryId.Value,
                        root.ProtectedHistoryId.Value,
                        root.Boundary.Value))
                    .ToArray();

                var active = histories
                    .SelectMany(history => _activeHistoryBoundaries
                        .ListBoundaries(new Core.Identifiers.HistoryId(history.HistoryId))
                        .Select(boundary => new ResearchActiveRetentionBoundarySnapshot(
                            history.HistoryId,
                            boundary.Value)))
                    .OrderBy(item => item.ProtectedHistoryId)
                    .ThenBy(item => item.Boundary)
                    .ToArray();

                return new ResearchRetentionSnapshot(histories, roots, active);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private static ResearchHistoryRetentionSnapshot CaptureHistory(
        Guid historyId,
        CommitSequence retentionFloor,
        CommitSequence currentSequence,
        IReadOnlyList<CommittedVersionSnapshot> versions)
    {
        var captured = versions
            .Select(version =>
            {
                var keyBytes = version.Key.ToArray();
                var keyId = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
                var versionId = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{historyId:N}:{version.CommitSequence.Value}:{version.TransactionId.Value:N}:{keyId}");
                return new ResearchCommittedVersionSnapshot(
                    versionId,
                    version.TransactionId.Value,
                    version.CommitSequence.Value,
                    keyId,
                    keyBytes.Length,
                    version.Value.Length,
                    version.IsDelete);
            })
            .OrderBy(version => version.CommitSequence)
            .ThenBy(version => version.KeyId, StringComparer.Ordinal)
            .ToArray();

        return new ResearchHistoryRetentionSnapshot(
            historyId,
            retentionFloor.Value,
            currentSequence.Value,
            captured);
    }
}
