using ChronicleDB.Core.Identifiers;
using ChronicleDB.Storage.Files;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.Recovery;

/// <summary>
/// Reconstructs the final committed logical state from complete WAL transactions.
/// </summary>
public static class WalRecovery
{
    public static RecoveryResult Reconcile(PersistentKeyValueStore store, WalLog wal)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wal);

        var active = new Dictionary<TransactionId, List<StorageMutation>>();
        var seenTransactions = new HashSet<TransactionId>();
        var committed = new List<(ulong Lsn, List<StorageMutation> Mutations)>();
        foreach (var record in wal.ReadAll())
        {
            switch (record.Type)
            {
                case WalRecordType.Begin:
                    if (!seenTransactions.Add(record.TransactionId) || !active.TryAdd(record.TransactionId, []))
                    {
                        throw new WalCorruptionException("A transaction appears twice in the WAL without completing.");
                    }

                    break;
                case WalRecordType.Put:
                    GetActive(active, record.TransactionId).Add(ToStorageMutation(WalMutationCodec.DecodePut(record.Payload.Span)));
                    break;
                case WalRecordType.Delete:
                    GetActive(active, record.TransactionId).Add(ToStorageMutation(WalMutationCodec.DecodeDelete(record.Payload.Span)));
                    break;
                case WalRecordType.Commit:
                    if (!active.Remove(record.TransactionId, out var mutations))
                    {
                        throw new WalCorruptionException("A WAL commit has no active transaction.");
                    }

                    committed.Add((record.Lsn, mutations));
                    break;
                case WalRecordType.Abort:
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
        foreach (var transaction in committed.OrderBy(entry => entry.Lsn))
        {
            foreach (var mutation in transaction.Mutations)
            {
                finalState[mutation.Key] = mutation;
            }
        }

        var mutationsToApply = finalState.Values.ToArray();
        store.ApplyBatch(mutationsToApply);
        return new RecoveryResult(committed.Count, active.Count, mutationsToApply.Length);
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

public sealed record RecoveryResult(
    int CommittedTransactionCount,
    int IncompleteTransactionCount,
    int FinalMutationCount);
