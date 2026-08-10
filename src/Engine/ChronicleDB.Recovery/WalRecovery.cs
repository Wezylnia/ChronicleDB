using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Files;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.Recovery;

/// <summary>
/// Reconstructs committed logical state and v0.3 commit history from complete WAL transactions.
/// </summary>
public static class WalRecovery
{
    public static RecoveryResult Reconcile(PersistentKeyValueStore store, WalLog wal)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wal);

        var active = new Dictionary<TransactionId, List<StorageMutation>>();
        var seenTransactions = new HashSet<TransactionId>();
        var committed = new List<RecoveredTransaction>();
        var currentCommitSequence = CommitSequence.Initial;
        long? previousRecoveryBase = null;

        foreach (var record in wal.ReadAll())
        {
            switch (record.Type)
            {
                case WalRecordType.Begin:
                    RequireEmptyPayload(record, "begin");
                    if (!seenTransactions.Add(record.TransactionId) || !active.TryAdd(record.TransactionId, []))
                    {
                        throw new WalCorruptionException("A transaction appears twice in the WAL without completing.");
                    }

                    break;
                case WalRecordType.Put:
                    GetActive(active, record.TransactionId)
                        .Add(ToStorageMutation(WalMutationCodec.DecodePut(record.Payload.Span)));
                    break;
                case WalRecordType.Delete:
                    GetActive(active, record.TransactionId)
                        .Add(ToStorageMutation(WalMutationCodec.DecodeDelete(record.Payload.Span)));
                    break;
                case WalRecordType.Commit:
                    if (!active.Remove(record.TransactionId, out var mutations))
                    {
                        throw new WalCorruptionException("A WAL commit has no active transaction.");
                    }

                    var commitInfo = DecodeCommitInfo(record, currentCommitSequence);
                    ValidateRecoveryBase(commitInfo.BaseDataLength, previousRecoveryBase, store.DataLength);
                    if (commitInfo.BaseDataLength is { } recoveryBase)
                    {
                        previousRecoveryBase = recoveryBase;
                    }

                    var commitSequence = commitInfo.CommitSequence;
                    if (commitSequence <= currentCommitSequence)
                    {
                        throw new WalCorruptionException("WAL commit sequences must be strictly increasing.");
                    }

                    currentCommitSequence = commitSequence;
                    committed.Add(new RecoveredTransaction(
                        record.TransactionId,
                        record.Lsn,
                        commitSequence,
                        commitInfo.BaseDataLength,
                        mutations.ToArray()));
                    break;
                case WalRecordType.Abort:
                    RequireEmptyPayload(record, "abort");
                    if (!active.Remove(record.TransactionId))
                    {
                        throw new WalCorruptionException("A WAL abort has no active transaction.");
                    }

                    break;
                default:
                    throw new WalCorruptionException("WAL contains an unsupported record type.");
            }
        }

        var finalState = new Dictionary<ChronicleDB.Core.Keys.BinaryKey, StorageMutation>();
        foreach (var transaction in committed.OrderBy(entry => entry.CommitLsn))
        {
            foreach (var mutation in transaction.Mutations)
            {
                finalState[mutation.Key] = mutation;
            }
        }

        var mutationsToApply = finalState.Values.ToArray();
        // A partial/torn final physical page is never trusted. Keep it intact while
        // opening, and only discard it after WAL proves that committed history exists
        // from which the append-only tail can be reconstructed.
        if (store.HasUntrustedTail)
        {
            if (committed.Count == 0)
            {
                throw new ChronicleDB.Storage.StorageCorruptionException(
                    "The storage file has an untrusted tail without a durable WAL decision.");
            }

            var latest = committed[^1];
            if (latest.BaseDataLength is { } recoveryBase && store.CanRepairFrom(recoveryBase))
            {
                store.DiscardUntrustedTail(recoveryBase);
            }
            else if (latest.BaseDataLength is null
                     && store.UntrustedTailIsFinalAppend
                     && store.UntrustedTailIsPartialPage)
            {
                // Legacy v0.2 commits did not persist their pre-publication data length.
                // Without a recovery base, only a physically incomplete final page is
                // strong evidence of an interrupted append. A full-sized corrupt page
                // might predate the WAL and must remain a hard corruption error.
                store.DiscardUntrustedTail();
            }
            else
            {
                throw new ChronicleDB.Storage.StorageCorruptionException(
                    "The damaged storage region predates the latest durable commit and cannot be treated as a crash tail.");
            }
        }

        store.ApplyBatch(mutationsToApply);
        return new RecoveryResult(
            committed.Count,
            active.Count,
            mutationsToApply.Length)
        {
            CurrentCommitSequence = currentCommitSequence,
            CommittedTransactions = committed.ToArray()
        };
    }

    private static WalCommitInfo DecodeCommitInfo(WalRecord record, CommitSequence previous)
    {
        if (record.Payload.IsEmpty)
        {
            // Backward compatibility for v0.2 WALs whose Commit record had no payload.
            try
            {
                return new WalCommitInfo(previous.Next(), null);
            }
            catch (OverflowException)
            {
                throw new WalCorruptionException(
                    "Legacy WAL commit history exceeds the supported commit-sequence range.");
            }
        }

        return WalCommitCodec.Decode(record.Payload.Span);
    }

    private static void ValidateRecoveryBase(
        long? recoveryBase,
        long? previousRecoveryBase,
        long currentDataLength)
    {
        if (recoveryBase is not { } value)
        {
            return;
        }

        if (value < 0
            || value % StorageOptions.DefaultPageSize != 0
            || value > currentDataLength
            || previousRecoveryBase is { } previous && value < previous)
        {
            throw new WalCorruptionException("WAL commit recovery base is inconsistent with append-only storage history.");
        }
    }

    private static void RequireEmptyPayload(WalRecord record, string recordName)
    {
        if (!record.Payload.IsEmpty)
        {
            throw new WalCorruptionException($"WAL {recordName} record must not contain a payload.");
        }
    }

    private static List<StorageMutation> GetActive(
        Dictionary<TransactionId, List<StorageMutation>> active,
        TransactionId transactionId)
    {
        if (!active.TryGetValue(transactionId, out var mutations))
        {
            throw new WalCorruptionException("A WAL mutation has no active transaction.");
        }

        return mutations;
    }

    private static StorageMutation ToStorageMutation(WalMutation mutation)
        => new(mutation.Key, mutation.IsDelete, mutation.Value.Span);
}

public sealed record RecoveredTransaction(
    TransactionId TransactionId,
    ulong CommitLsn,
    CommitSequence CommitSequence,
    long? BaseDataLength,
    IReadOnlyList<StorageMutation> Mutations);

public sealed record RecoveryResult(
    int CommittedTransactionCount,
    int IncompleteTransactionCount,
    int FinalMutationCount)
{
    public CommitSequence CurrentCommitSequence { get; init; } = CommitSequence.Initial;

    public IReadOnlyList<RecoveredTransaction> CommittedTransactions { get; init; } = [];
}
