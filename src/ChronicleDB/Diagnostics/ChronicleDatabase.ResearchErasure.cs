using System.Security.Cryptography;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.History.Roots;
using ChronicleDB.Storage.History;
using ChronicleDB.Transactions.Mvcc;
using ChronicleDB.Wal.Branches;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB;

public sealed partial class ChronicleDatabase
{
    /// <summary>
    /// Captures a key-specific erasure closure input without changing visibility,
    /// retention, or recovery authority. WAL/checkpoint occurrences are decoded
    /// exactly. Historical/stale bytes inside append-oriented data pages are not yet
    /// decoded by v1.1, so physical closure is explicitly marked incomplete.
    /// </summary>
    public ErasureClosureInput CaptureResearchErasureClosureInput(
        ReadOnlySpan<byte> key,
        Guid? originHistoryId = null)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Research erasure capture requires a non-empty key.", nameof(key));
        }

        var keyBytes = key.ToArray();
        var keyId = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        EnterOperation();
        try
        {
            lock (_historyGate)
            {
                var topology = new List<ErasureHistoryNode>(_branchRuntimes.Count + 1)
                {
                    new(_mainHistoryId.Value, null),
                };
                topology.AddRange(_branchRuntimes.Values
                    .OrderBy(runtime => runtime.Definition.Depth)
                    .ThenBy(runtime => runtime.Definition.HistoryId.Value)
                    .Select(runtime => new ErasureHistoryNode(
                        runtime.Definition.HistoryId.Value,
                        runtime.Definition.ParentHistoryId.Value)));

                var origin = originHistoryId ?? _mainHistoryId.Value;
                if (!topology.Any(node => node.HistoryId == origin))
                {
                    throw new ArgumentOutOfRangeException(nameof(originHistoryId), "Origin history does not exist.");
                }

                var historyVersions = new Dictionary<Guid, IReadOnlyList<CommittedVersionSnapshot>>
                {
                    [_mainHistoryId.Value] = _versions.SnapshotHistory(),
                };
                foreach (var runtime in _branchRuntimes.Values)
                {
                    historyVersions[runtime.Definition.HistoryId.Value] = runtime.Versions.SnapshotHistory();
                }

                var representations = new List<ErasureRepresentation>();
                foreach (var pair in historyVersions.OrderBy(pair => pair.Key))
                {
                    AddVersionRepresentations(representations, pair.Key, pair.Value, keyBytes);
                }

                foreach (var root in _historyRoots.ListActive().OrderBy(root => root.RootId.Value))
                {
                    if (!historyVersions.TryGetValue(root.ProtectedHistoryId.Value, out var versions))
                    {
                        continue;
                    }

                    var visible = versions
                        .Where(version => version.CommitSequence.Value <= root.Boundary.Value
                            && version.Key.AsSpan().SequenceEqual(keyBytes))
                        .OrderBy(version => version.CommitSequence.Value)
                        .LastOrDefault();
                    var content = visible is null
                        ? ErasureContentState.Absent
                        : visible.IsDelete ? ErasureContentState.Tombstone : ErasureContentState.Value;
                    var kind = root.Kind switch
                    {
                        HistoryRootKind.PersistentSnapshot => ErasureRepresentationKind.PersistentSnapshotRoot,
                        HistoryRootKind.BranchBase => ErasureRepresentationKind.BranchBaseRoot,
                        HistoryRootKind.ActiveTransaction => ErasureRepresentationKind.ActiveTransactionRoot,
                        _ => throw new InvalidOperationException($"Unsupported history root kind '{root.Kind}'."),
                    };
                    representations.Add(new ErasureRepresentation(
                        $"root:{root.RootId.Value:N}",
                        kind,
                        root.HistoryId.Value,
                        root.ProtectedHistoryId.Value,
                        root.Boundary.Value,
                        content,
                        IsObserverContract: true));
                }

                AddWalRepresentations(representations, _mainHistoryId.Value, branchId: null, _wal, keyBytes);
                foreach (var runtime in _branchRuntimes.Values)
                {
                    AddWalRepresentations(
                        representations,
                        runtime.Definition.HistoryId.Value,
                        runtime.Definition.BranchId,
                        runtime.Wal,
                        keyBytes);
                }

                AddCheckpointRepresentations(
                    representations,
                    _databaseDirectory,
                    _databaseId,
                    _mainHistoryId,
                    keyBytes);
                foreach (var runtime in _branchRuntimes.Values)
                {
                    AddCheckpointRepresentations(
                        representations,
                        runtime.Directory,
                        runtime.Store.DatabaseId,
                        runtime.Definition.HistoryId,
                        keyBytes);
                }

                var unscanned = new List<string>
                {
                    "chronicle.data/branch data historical or stale page bytes are not key-decoded by the v1.1 erasure probe",
                };
                foreach (var (directory, historyId) in EnumerateHistoryDirectories())
                {
                    foreach (var path in Directory.EnumerateFiles(directory)
                                 .Where(path => path.EndsWith(".creating", StringComparison.Ordinal)
                                     || path.EndsWith(".previous", StringComparison.Ordinal)
                                     || path.Contains("compaction", StringComparison.OrdinalIgnoreCase)))
                    {
                        representations.Add(new ErasureRepresentation(
                            $"temporary:{Path.GetFileName(path)}:{historyId:N}",
                            ErasureRepresentationKind.CompactionTemporary,
                            historyId,
                            historyId,
                            null,
                            ErasureContentState.Unknown,
                            IsObserverContract: false));
                        unscanned.Add(path);
                    }
                }

                return new ErasureClosureInput(
                    keyId,
                    origin,
                    Array.AsReadOnly(topology.ToArray()),
                    Array.AsReadOnly(representations.ToArray()),
                    PhysicalRepresentationScanComplete: false,
                    Array.AsReadOnly(unscanned.ToArray()));

                IEnumerable<(string Directory, Guid HistoryId)> EnumerateHistoryDirectories()
                {
                    yield return (_databaseDirectory, _mainHistoryId.Value);
                    foreach (var runtime in _branchRuntimes.Values)
                    {
                        yield return (runtime.Directory, runtime.Definition.HistoryId.Value);
                    }
                }
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private static void AddVersionRepresentations(
        List<ErasureRepresentation> target,
        Guid historyId,
        IReadOnlyList<CommittedVersionSnapshot> versions,
        byte[] key)
    {
        var matching = versions
            .Where(version => version.Key.AsSpan().SequenceEqual(key))
            .OrderBy(version => version.CommitSequence.Value)
            .ToArray();
        foreach (var version in matching)
        {
            target.Add(new ErasureRepresentation(
                $"version:{historyId:N}:{version.CommitSequence.Value}:{version.TransactionId.Value:N}",
                ErasureRepresentationKind.MvccVersion,
                historyId,
                historyId,
                version.CommitSequence.Value,
                version.IsDelete ? ErasureContentState.Tombstone : ErasureContentState.Value,
                IsObserverContract: false));
        }

        if (matching.Length != 0)
        {
            var latest = matching[^1];
            target.Add(new ErasureRepresentation(
                $"derived-current:{historyId:N}:{latest.CommitSequence.Value}",
                ErasureRepresentationKind.DerivedCurrentState,
                historyId,
                historyId,
                latest.CommitSequence.Value,
                latest.IsDelete ? ErasureContentState.Tombstone : ErasureContentState.Value,
                IsObserverContract: false));
        }
    }

    private static void AddWalRepresentations(
        List<ErasureRepresentation> target,
        Guid historyId,
        BranchId? branchId,
        WalLog wal,
        byte[] key)
    {
        foreach (var record in wal.ReadAll())
        {
            var payload = record.Payload;
            if (branchId is { } expectedBranchId)
            {
                payload = BranchWalEnvelopeCodec.Decode(
                    payload.Span,
                    expectedBranchId,
                    new HistoryId(historyId)).Payload;
            }

            WalMutation? mutation = record.Type switch
            {
                WalRecordType.Put => WalMutationCodec.DecodePut(payload.Span),
                WalRecordType.Delete => WalMutationCodec.DecodeDelete(payload.Span),
                _ => null,
            };
            if (mutation is null || !mutation.Key.AsSpan().SequenceEqual(key))
            {
                continue;
            }

            target.Add(new ErasureRepresentation(
                $"wal:{historyId:N}:{record.Lsn}:{record.TransactionId.Value:N}",
                ErasureRepresentationKind.WalMutation,
                historyId,
                historyId,
                record.Lsn,
                mutation.IsDelete ? ErasureContentState.Tombstone : ErasureContentState.Value,
                IsObserverContract: false));
        }
    }

    private static void AddCheckpointRepresentations(
        List<ErasureRepresentation> target,
        string directory,
        Guid databaseId,
        HistoryId historyId,
        byte[] key)
    {
        var checkpoint = PersistentHistoryCheckpoint.Inspect(directory, databaseId, historyId);
        if (checkpoint is null)
        {
            return;
        }

        foreach (var version in checkpoint.Versions
                     .Where(version => version.Key.AsSpan().SequenceEqual(key))
                     .OrderBy(version => version.CommitSequence.Value))
        {
            target.Add(new ErasureRepresentation(
                $"checkpoint:{historyId.Value:N}:{version.CommitSequence.Value}:{version.TransactionId.Value:N}",
                ErasureRepresentationKind.CheckpointVersion,
                historyId.Value,
                historyId.Value,
                version.CommitSequence.Value,
                version.IsDelete ? ErasureContentState.Tombstone : ErasureContentState.Value,
                IsObserverContract: false));
        }
    }
}
