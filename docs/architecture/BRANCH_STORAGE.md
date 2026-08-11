# v1.0 branch storage

ChronicleDB separates shared parent history, branch lifecycle metadata, branch-private MVCC versions, branch-private WAL, branch snapshots, and retained-history checkpoints. The ownership boundary is explicit so branch creation does not physically duplicate inherited state and recovery never confuses Main history with a branch history.

## Files and ownership

The Main database owns `chronicle.branches`, a checksummed lifecycle journal bound to the Main `DatabaseId` and Main `HistoryId`.

Each active branch owns:

```text
branches/<BranchId>/
  chronicle.data
  branch.wal
  chronicle.snapshots
  chronicle.history   # after retained-history checkpoint initialization
```

The branch-private storage GUID is recorded as `LocalStorageId`. Parent pages/records are not copied during branch creation. Unmodified keys remain reachable through the fixed parent base; modified keys create new branch-owned versions.

## Branch metadata framing

`chronicle.branches` uses a versioned/checksummed header, redundant record/footer lengths, CRC32C, and contiguous lifecycle event sequences. Complete corrupt records are fatal; only a proven incomplete final frame may be treated as a crash tail.

Current lifecycle/maintenance record types include:

- `CreateIntent`;
- `Activate`;
- `AdvanceSequence`;
- `AbandonCreate`;
- `DeleteIntent`;
- `DeleteComplete`;
- `PublishPhysicalBoundary`;
- maintenance-only `RestoreActive` when a lifecycle journal is compacted to canonical active state.

Branch IDs and history IDs are never silently reused.

## Branch version envelope

Each branch-local logical mutation is stored as a self-checking `BVR1` version envelope containing branch/history/transaction identity, local commit sequence, mutation index/count, full binary user key, tombstone/value state, and CRC32C. The binary-key contract matches Main, including the valid zero-length binary key. Physical storage keys identify version objects rather than user keys, so historical branch versions coexist in append-oriented storage.

## Transaction authority

In v1.0, `branch.wal` is transaction durability authority. Every generic WAL record payload is wrapped with `BranchId + HistoryId`, so cross-branch or Main-to-branch replay fails closed even if generic WAL framing is otherwise valid. The local `chronicle.data` file is derived physical state and can be repaired/rebuilt only after checkpoint/WAL logical authority has been validated.

`AdvanceSequence` and `PublishPhysicalBoundary` describe local current/physical publication progress; they cannot manufacture a commit that is absent from authoritative branch checkpoint/WAL history.

## Retained-history checkpoint

`chronicle.history` is an immutable checksummed projection of all MVCC versions still required by the branch generic floor, explicit persistent roots, process-local observers captured by maintenance, and latest current state. Publication is temp-file write + fsync + replace + re-read validation. WAL rotation is allowed only after an equivalent checkpoint is durable.

## Physical rewrite

Compaction first refreshes authoritative retained history, then uses copy-and-publish rewrite of branch-private data. Inherited parent state is never materialized into the branch file merely for compaction. A previous generation remains recoverable across the publication rename window and is retired only after the new primary validates.

## Deleted branch cleanup

`DeleteComplete` means the history is no longer openable; deleting the branch-private directory is a separate reclamation step. If directory deletion is temporarily blocked, the durable deleted record is retained so a later GC pass can retry. The branch lifecycle journal is not compacted past unresolved physical-cleanup obligations.

## v0.7 migration

Legacy v0.7 branches may contain only branch metadata committed-prefix descriptors and local version pages. Before v1.0 treats branch WAL as authoritative, that legacy state is fully validated and bootstrapped into a fsynced identity-bound WAL. The `WalInitialized` capability is published only after bootstrap succeeds; a partial pre-flag WAL is non-authoritative and is rebuilt on retry.
