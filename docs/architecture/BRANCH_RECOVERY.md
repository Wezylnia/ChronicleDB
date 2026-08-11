# Branch recovery

Branch recovery is dependency-ordered and fail-closed. Main metadata and history roots are validated before any branch becomes available; parent histories therefore exist before their children are exposed.

## Recovery inputs

For each active branch, recovery validates:

- persistent branch identity and ancestry;
- branch-local storage identity;
- the fixed parent base root;
- branch-local history checkpoint, when initialized;
- `branch.wal` framing and per-record branch/history identity;
- branch-local snapshots;
- the derived branch physical version store.

The logical authority is `checkpoint + branch WAL`. Branch physical pages are derived state and may be redone from that authority when a durable transaction was not completely published before a crash.

## Recovery algorithm

1. load the retained-history checkpoint when the capability flag requires it;
2. rebuild retained MVCC versions from the checkpoint;
3. scan branch WAL and validate transaction structure, contiguous LSNs, commit-sequence ordering, identity envelopes, and physical recovery bases;
4. replay WAL commits newer than the checkpoint into MVCC state;
5. reject lifecycle metadata that claims commits absent from checkpoint/WAL history;
6. classify any physical append tail against the durably published physical prefix;
7. redo WAL commits newer than branch metadata into the physical branch store;
8. converge branch metadata to the WAL sequence;
9. validate every retained physical version byte-for-byte against logical history;
10. load branch snapshot metadata and expose the branch only after all checks succeed.

Physical versions below an advanced generic retention floor may remain temporarily until compaction; unexplained records inside the retained range are corruption.

Because the branch physical store is derived state after v0.8, checksum/framing damage in that store may be rebuilt from an already validated checkpoint + branch WAL authority. This recovery rule does not make valid-looking but semantically inconsistent pages trustworthy: if a decoded physical version has the wrong logical key identity, commit sequence, transaction identity, tombstone state, or value bytes, open fails closed with corruption instead of silently rewriting it.

## Deletion recovery

Branch deletion uses durable DeleteIntent and DeleteComplete lifecycle records. New branch operations are blocked once deletion starts. Normal deletion is rejected while open handles, active transactions, persistent branch snapshots, or child branches remain.

If a crash leaves a durable DeleteIntent, reopen completes the deletion only when the persistent dependency graph is still safe. A delete intent coexisting with a retained child or branch snapshot is treated as corruption rather than guessed away. Branch-private files are reclaimed later by v0.9 GC.

## Ancestry

Every branch has exactly one parent and ancestry must remain acyclic. Missing parents, self-parenting, invalid base sequences, wrong database identities, or inconsistent depth prevent the affected database from opening normally.
