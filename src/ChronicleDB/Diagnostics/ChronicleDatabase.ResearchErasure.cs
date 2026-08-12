using System.Security.Cryptography;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Keys;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.History.Roots;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;
using ChronicleDB.Storage.Files;
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
    /// retention, or recovery authority. WAL/checkpoint occurrences and every
    /// structurally decodable record in engine-controlled current/previous/compaction
    /// data generations are scanned without consulting the live key index.
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

                var unscanned = new List<string>();
                var physicalScanComplete = AddPhysicalDataRepresentations(
                    representations,
                    unscanned,
                    _store.CapturePhysicalDataRecords(),
                    _mainHistoryId.Value,
                    branchId: null,
                    keyBytes,
                    "main/current");
                physicalScanComplete &= ScanAdditionalDataGenerations(
                    representations,
                    unscanned,
                    _store,
                    _databaseDirectory,
                    _mainHistoryId.Value,
                    branchId: null,
                    keyBytes,
                    "main");

                foreach (var runtime in _branchRuntimes.Values.OrderBy(item => item.Definition.HistoryId.Value))
                {
                    var label = $"branch-{runtime.Definition.BranchId.Value:N}";
                    physicalScanComplete &= AddPhysicalDataRepresentations(
                        representations,
                        unscanned,
                        runtime.Store.CapturePhysicalDataRecords(),
                        runtime.Definition.HistoryId.Value,
                        runtime.Definition.BranchId,
                        keyBytes,
                        label + "/current");
                    physicalScanComplete &= ScanAdditionalDataGenerations(
                        representations,
                        unscanned,
                        runtime.Store,
                        runtime.Directory,
                        runtime.Definition.HistoryId.Value,
                        runtime.Definition.BranchId,
                        keyBytes,
                        label);
                }

                return new ErasureClosureInput(
                    keyId,
                    origin,
                    Array.AsReadOnly(topology.ToArray()),
                    Array.AsReadOnly(representations.ToArray()),
                    PhysicalRepresentationScanComplete: physicalScanComplete,
                    Array.AsReadOnly(unscanned.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private static bool ScanAdditionalDataGenerations(
        List<ErasureRepresentation> target,
        List<string> unscanned,
        PersistentKeyValueStore store,
        string historyDirectory,
        Guid historyId,
        BranchId? branchId,
        byte[] key,
        string sourcePrefix)
    {
        var complete = true;
        var previous = Path.Combine(historyDirectory, PersistentKeyValueStore.DataFileName + ".previous");
        if (File.Exists(previous))
        {
            complete &= AddPhysicalDataRepresentations(
                target,
                unscanned,
                store.CapturePhysicalDataRecords(previous),
                historyId,
                branchId,
                key,
                sourcePrefix + "/previous");
        }

        foreach (var compactDirectory in Directory.EnumerateDirectories(historyDirectory, ".compact-*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var dataPath = Path.Combine(compactDirectory, PersistentKeyValueStore.DataFileName);
            if (!File.Exists(dataPath))
            {
                continue;
            }

            var compactId = Path.GetFileName(compactDirectory);
            complete &= AddPhysicalDataRepresentations(
                target,
                unscanned,
                store.CapturePhysicalDataRecords(dataPath),
                historyId,
                branchId,
                key,
                sourcePrefix + "/" + compactId);
        }

        return complete;
    }

    private static bool AddPhysicalDataRepresentations(
        List<ErasureRepresentation> target,
        List<string> unscanned,
        PhysicalDataFileScanResult scan,
        Guid historyId,
        BranchId? branchId,
        byte[] key,
        string sourceLabel)
    {
        var complete = scan.IsComplete;
        foreach (var issue in scan.Issues)
        {
            unscanned.Add(issue);
        }

        foreach (var physical in scan.Records)
        {
            ErasureContentState content;
            ulong? sequence = null;
            if (branchId is null)
            {
                if (!physical.PhysicalKey.AsSpan().SequenceEqual(key))
                {
                    continue;
                }

                content = physical.IsStorageTombstone
                    ? ErasureContentState.Tombstone
                    : ErasureContentState.Value;
            }
            else
            {
                if (physical.IsStorageTombstone)
                {
                    // Store-level tombstones carry no logical value bytes and therefore
                    // cannot themselves reconstruct the target key. The prior put page, if
                    // present, is scanned independently and remains represented.
                    continue;
                }

                BranchVersionRecord record;
                try
                {
                    record = BranchVersionRecordCodec.Decode(physical.Value);
                }
                catch (StorageException exception)
                {
                    complete = false;
                    unscanned.Add(
                        $"{scan.SourceName}: branch record page {physical.RecordPageId.Value} " +
                        $"could not be interpreted as a logical branch version: {exception.Message}");
                    continue;
                }

                if (record.BranchId != branchId.Value || record.HistoryId.Value != historyId)
                {
                    complete = false;
                    unscanned.Add(
                        $"{scan.SourceName}: branch record page {physical.RecordPageId.Value} " +
                        "belongs to a different branch/history identity.");
                    continue;
                }

                if (!record.Key.AsSpan().SequenceEqual(key))
                {
                    continue;
                }

                content = record.IsDelete ? ErasureContentState.Tombstone : ErasureContentState.Value;
                sequence = record.CommitSequence.Value;
            }

            var recordId = $"physical:{historyId:N}:{sourceLabel}:page-{physical.RecordPageId.Value}";
            target.Add(new ErasureRepresentation(
                recordId,
                ErasureRepresentationKind.PhysicalDataRecord,
                historyId,
                historyId,
                sequence,
                content,
                IsObserverContract: false));

            if (content != ErasureContentState.Value)
            {
                continue;
            }

            foreach (var overflowPage in physical.OverflowPages)
            {
                target.Add(new ErasureRepresentation(
                    $"physical-overflow:{historyId:N}:{sourceLabel}:record-{physical.RecordPageId.Value}:page-{overflowPage.Value}",
                    ErasureRepresentationKind.PhysicalOverflowChunk,
                    historyId,
                    historyId,
                    sequence,
                    ErasureContentState.Value,
                    IsObserverContract: false));
            }
        }

        return complete;
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
