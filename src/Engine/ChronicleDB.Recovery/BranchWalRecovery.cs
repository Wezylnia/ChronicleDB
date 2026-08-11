using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Storage.Files;
using ChronicleDB.Wal.Branches;
using ChronicleDB.Wal.Errors;
using ChronicleDB.Wal.Files;
using ChronicleDB.Wal.Records;

namespace ChronicleDB.Recovery;

/// <summary>
/// Validates and reconstructs one branch-local WAL stream. A branch WAL is a normal
/// framed WAL whose every payload is additionally bound to BranchId + HistoryId.
/// Commit sequences are monotonic inside that history and may start after a durable
/// history checkpoint.
/// </summary>
public static class BranchWalRecovery
{
    public static BranchRecoveryResult ReadCommitted(
        WalLog wal,
        BranchId expectedBranchId,
        HistoryId expectedHistoryId,
        CommitSequence checkpointSequence,
        int pageSize,
        IReadOnlySet<TransactionId>? checkpointTransactionIds = null)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (!expectedBranchId.IsValid || !expectedHistoryId.IsValid)
        {
            throw new ArgumentException("Branch WAL recovery requires valid logical identities.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var active = new Dictionary<TransactionId, List<StorageMutation>>();
        var seen = new HashSet<TransactionId>();
        var committed = new List<RecoveredBranchTransaction>();
        var latestSequence = checkpointSequence;
        var lastSeenCommit = CommitSequence.Initial;
        var sawPreResetCheckpointGeneration = false;
        var sawPostResetGeneration = false;
        var highestCheckpointGenerationCommit = CommitSequence.Initial;
        long? previousBase = null;

        foreach (var record in wal.ReadAll())
        {
            var envelope = BranchWalEnvelopeCodec.Decode(record.Payload.Span);
            if (envelope.BranchId != expectedBranchId || envelope.HistoryId != expectedHistoryId)
            {
                throw new WalCorruptionException("Branch WAL record belongs to another branch or history domain.");
            }

            switch (record.Type)
            {
                case WalRecordType.Begin:
                    RequireEmpty(envelope.Payload.Span, "begin");
                    if (!seen.Add(record.TransactionId) || !active.TryAdd(record.TransactionId, []))
                    {
                        throw new WalCorruptionException("A branch transaction identity is reused in the WAL.");
                    }
                    break;
                case WalRecordType.Put:
                    GetActive(active, record.TransactionId).Add(ToStorageMutation(
                        WalMutationCodec.DecodePut(envelope.Payload.Span)));
                    break;
                case WalRecordType.Delete:
                    GetActive(active, record.TransactionId).Add(ToStorageMutation(
                        WalMutationCodec.DecodeDelete(envelope.Payload.Span)));
                    break;
                case WalRecordType.Commit:
                    if (!active.Remove(record.TransactionId, out var mutations))
                    {
                        throw new WalCorruptionException("A branch WAL commit has no active transaction.");
                    }
                    var info = WalCommitCodec.Decode(envelope.Payload.Span);
                    if (!lastSeenCommit.IsInitial && info.CommitSequence <= lastSeenCommit)
                    {
                        throw new WalCorruptionException("Branch WAL commit sequences are not strictly increasing.");
                    }
                    lastSeenCommit = info.CommitSequence;
                    ValidateRecoveryBase(info.BaseDataLength, previousBase, pageSize);
                    if (info.BaseDataLength is { } baseLength)
                    {
                        previousBase = baseLength;
                    }

                    // A checkpoint may coexist with the pre-reset WAL after a crash.
                    // The old generation must terminate exactly at that checkpoint. After
                    // reset, every commit belongs strictly above the checkpoint. Accepting a
                    // mixture would let stale/corrupt branch WAL records evade replay checks.
                    if (info.CommitSequence <= checkpointSequence)
                    {
                        if (sawPostResetGeneration)
                        {
                            throw new WalCorruptionException(
                                "Branch WAL mixes pre-reset checkpoint history with post-reset commits.");
                        }
                        sawPreResetCheckpointGeneration = true;
                        highestCheckpointGenerationCommit = info.CommitSequence;
                        break;
                    }
                    if (sawPreResetCheckpointGeneration)
                    {
                        throw new WalCorruptionException(
                            "Branch WAL mixes pre-reset checkpoint history with post-reset commits.");
                    }
                    sawPostResetGeneration = true;

                    if (checkpointTransactionIds?.Contains(record.TransactionId) == true)
                    {
                        throw new WalCorruptionException(
                            "A branch transaction identity is reused across the durable history checkpoint and post-checkpoint WAL.");
                    }

                    if (info.CommitSequence <= latestSequence)
                    {
                        throw new WalCorruptionException("Branch WAL attempts to replay history at or below the recovery base.");
                    }

                    latestSequence = info.CommitSequence;
                    committed.Add(new RecoveredBranchTransaction(
                        record.TransactionId,
                        record.Lsn,
                        info.CommitSequence,
                        info.BaseDataLength,
                        mutations.ToArray()));
                    break;
                case WalRecordType.Abort:
                    RequireEmpty(envelope.Payload.Span, "abort");
                    if (!active.Remove(record.TransactionId))
                    {
                        throw new WalCorruptionException("A branch WAL abort has no active transaction.");
                    }
                    break;
                default:
                    throw new WalCorruptionException("Branch WAL contains an unsupported record type.");
            }
        }

        if (sawPreResetCheckpointGeneration
            && highestCheckpointGenerationCommit != checkpointSequence)
        {
            throw new WalCorruptionException(
                "A pre-reset branch WAL generation does not reach the durable checkpoint boundary.");
        }

        return new BranchRecoveryResult(latestSequence, active.Count, committed);
    }

    private static void ValidateRecoveryBase(long? value, long? previous, int pageSize)
    {
        if (value is not { } length)
        {
            return;
        }
        if (length < 0 || length % pageSize != 0 || previous is { } p && length < p)
        {
            throw new WalCorruptionException("Branch WAL physical recovery bases are invalid or non-monotonic.");
        }
    }

    private static List<StorageMutation> GetActive(
        Dictionary<TransactionId, List<StorageMutation>> active,
        TransactionId transactionId)
        => active.TryGetValue(transactionId, out var mutations)
            ? mutations
            : throw new WalCorruptionException("A branch WAL mutation has no active transaction.");

    private static StorageMutation ToStorageMutation(WalMutation mutation)
        => new(mutation.Key, mutation.IsDelete, mutation.Value.Span);

    private static void RequireEmpty(ReadOnlySpan<byte> payload, string name)
    {
        if (!payload.IsEmpty)
        {
            throw new WalCorruptionException($"Branch WAL {name} record must not carry an inner payload.");
        }
    }
}

public sealed record RecoveredBranchTransaction(
    TransactionId TransactionId,
    ulong CommitLsn,
    CommitSequence CommitSequence,
    long? BaseDataLength,
    IReadOnlyList<StorageMutation> Mutations);

public sealed record BranchRecoveryResult(
    CommitSequence CurrentCommitSequence,
    int IncompleteTransactionCount,
    IReadOnlyList<RecoveredBranchTransaction> CommittedTransactions);
