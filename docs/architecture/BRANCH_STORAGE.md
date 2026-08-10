# v0.7 branch storage

v0.7 separates shared parent history, branch identity metadata, branch-private versions, and branch-private snapshot metadata.

## Files and ownership

The Main database owns `chronicle.branches`, an append-only branch metadata journal bound to the Main `DatabaseId` and Main `HistoryId`.

Each branch owns a directory:

```text
branches/<BranchId>/
```

The directory contains an independent `PersistentKeyValueStore` used as an append-only physical version store plus the normal snapshot metadata file for snapshots created in that branch. Its persistent storage GUID is recorded as `LocalStorageId` in branch activation metadata.

Parent pages are never copied merely because a branch is created. Unmodified state remains reachable through the parent historical root. Modified state is represented by newly appended branch-owned version records.

## Branch metadata framing

`chronicle.branches` has a versioned, checksummed, database-bound header. Records use redundant total-length framing, CRC32C, a footer length/magic, and a contiguous event sequence. A complete corrupt record is fatal. Only an incomplete final frame may be truncated as a crash tail.

Record types are:

- `CreateIntent` — reserves immutable identity, ancestry, base root, depth, and name;
- `Activate` — binds the branch to a branch-local storage identity;
- `AdvanceSequence` — publishes one local commit sequence and its committed data-file prefix;
- `AbandonCreate` — closes an incomplete creation and releases its name.

Branch IDs and history IDs are never reused after appearing in the journal, even when an incomplete create is abandoned.

## Branch version envelope

Each logical local write is encoded in a self-checking `BranchVersionRecord` containing:

- `BranchId` and branch `HistoryId`;
- creator `TransactionId`;
- local `CommitSequence`;
- mutation index and mutation count;
- full logical key bytes;
- tombstone/value state;
- CRC32C-protected framing.

The physical key is derived from `(local sequence, transaction ID, mutation index)`, so each committed logical version occupies a distinct append-only record. User-key identity remains the full binary key inside the envelope.

## Recovery boundary

The latest durable `AdvanceSequence` descriptor is authoritative for v0.7 branch-local state. It records `DataLengthAfterCommit`. During open:

1. a shorter local file is corruption because committed bytes are missing;
2. a longer or torn local tail may be truncated only when the first untrusted byte is at or after the published prefix; corruption inside the published prefix is fatal;
3. every retained version envelope must match a published commit descriptor;
4. mutation indexes/counts and local sequences must be complete and contiguous;
5. the local MVCC version store is rebuilt in local commit order.

This mechanism intentionally provides a conservative v0.7 committed-prefix baseline. It does not replace the independent branch WAL/recovery design scheduled for v0.8.
