# Recovery Model

Opening a ChronicleDB database is a recovery operation. Application work is not exposed until storage metadata, WAL, current physical state, committed version history, persistent snapshot metadata, and the generalized history-root registry have all been validated and reconstructed.

## Open order

1. atomically create or validate the database metadata journal;
2. validate immutable database identity and page format;
3. require WAL/snapshot/history-root files when durable capability flags say they were already initialized;
4. open and validate the database-bound WAL;
5. scan WAL framing, contiguous LSNs, transaction structure, commit sequences, and recovery-base metadata;
6. classify any untrusted physical data tail;
7. if WAL proves the latest append region is redoable, truncate only to the proven pre-publication base;
8. reconcile final committed current state into append-only storage;
9. rebuild complete in-memory MVCC chains from committed WAL transactions;
10. on the one-time path where WAL was not previously initialized, persist one synthetic bootstrap transaction for physical keys not represented in WAL; if WAL was already initialized, such out-of-band keys are corruption;
11. open/scan persistent snapshot metadata and recover an incomplete framed tail only;
12. open/scan the history-root registry and recover an incomplete framed tail only;
13. open/scan `chronicle.branches`, resolve incomplete branch-creation intents, validate ancestry, and reconcile branch-base roots;
14. validate and reconstruct every active branch-local committed prefix and its branch snapshots;
15. reconcile Main and branch snapshot roots into the generalized registry;
16. validate all root boundaries and branch/history ownership against recovered histories;
17. expose the database as `Open`.

## WAL commit compatibility

Recovery accepts legacy commit payloads used during development:

- empty: v0.2, sequence assigned in commit order, no physical recovery base;
- 8 bytes: sequence-only early v0.3;
- 16 bytes: current sequence + pre-publication data length.

Recovered commit sequences must be strictly increasing.

## Physical crash-tail repair

The storage scanner may stop at a corrupt page in recovery mode without immediately changing the file. Current commits prove their physical append region with the pre-publication data length embedded in the durable Commit record. Recovery may truncate to that base only when it is an aligned prefix no newer than the first untrusted byte.

Older complete-page corruption that predates the latest proven append region remains fatal. Legacy commits without a recovery base may repair only an actually partial final page. This prevents arbitrary corruption from being mislabeled as a crash tail.

## Snapshot metadata recovery

`chronicle.snapshots` is self-framed and checksummed. A short final record may be truncated. A complete header with inconsistent redundant length fields, an invalid checksum, sequence gap, ID reuse, duplicate active name, wrong database identity, or invalid lifecycle transition is corruption.

A snapshot create/delete that was flushed before a process crash is recovered even if the caller never received acknowledgement. A pre-flush crash may recover either the previous or new complete metadata state, never a partially decoded root.

## History-root metadata recovery

`chronicle.history-roots` is database-bound, self-framed, checksummed, and event-sequence ordered. A short final record is truncated. A complete record with an invalid checksum, unsupported identity, sequence gap, reused root ID, or mismatched delete metadata is corruption. Snapshot roots are reconciled against `chronicle.snapshots`: a snapshot with a missing root is repaired by appending one active root record, while an orphaned active snapshot root is durably tombstoned. This makes the two-file publication window deterministic without silently losing retention.

The durable root protocol publishes only complete Active or Deleted outcomes. The in-memory registry may hold Creating or Deleting intents while an operation is in flight; those intents retain history conservatively and are resolved by reopening after an uncertain operation.

## Checkpoint evolution

v0.5 deliberately did **not** truncate historical WAL because no independent retained-history representation existed yet. The v1.0 retained-history protocol uses `chronicle.history`: WAL rotation is permitted only after an equivalent retained MVCC projection has been written, fsynced, re-read successfully, and its capability flag has become durable.


## Legacy v0.7 branch reopen compatibility

A legacy v0.7 branch is reconstructed from its branch metadata plus branch-private append store. The latest `AdvanceSequence` record identifies the local commit sequence and exact `DataLengthAfterCommit`. A local file shorter than that boundary is corruption. A longer/torn tail is reduced to the published prefix only when its first untrusted byte is not inside that committed prefix; corruption within the published prefix is fatal. Every retained local version envelope must then match a published transaction descriptor and complete mutation index set, and the in-memory MVCC chains are replayed by local commit sequence.

This committed-prefix protocol remains a compatibility path only. Current v1.0 branches use logically independent branch WAL streams, branch-specific replay, and crash-safe lifecycle recovery.

## Branch recovery

Every active branch is recovered only after its parent/base metadata is validated. `branch.wal` is scanned with per-record `BranchId` and `HistoryId` verification. A durable commit absent from the branch physical store is redone; lifecycle metadata may be advanced to match WAL, but may never claim a commit not present in checkpoint/WAL history. Incomplete transactions are ignored. Missing initialized branch WAL or cross-history WAL data is corruption.

Branch delete intents are reconciled before branch runtimes are exposed. A durable delete intent is completed only when no persistent child/snapshot dependency contradicts it.

## Retained-history checkpoints

`chronicle.history` is a complete checksummed retained MVCC projection for one history domain. Recovery loads it first, then validates/replays only WAL commits newer than its checkpoint sequence. If both the primary checkpoint and `.previous` exist, a fully validated primary is authoritative; failure to remove the older generation cannot roll recovery backward. The previous generation is restored only when the canonical checkpoint file is absent after an interrupted publication rename. A present but invalid primary fails closed; recovery does not guess that an older checkpoint is still compatible with the current WAL generation. A pre-reset WAL may coexist with a newly published checkpoint after a crash, but that WAL generation must end **exactly** at the checkpoint sequence. A post-reset WAL may contain only commits newer than the checkpoint; one WAL generation may never mix pre-reset and post-reset commits. Transaction identities retained by the checkpoint may not be reused by post-checkpoint WAL transactions.

Main current physical state is byte-for-byte validated against the latest recovered logical MVCC state. Branch physical state is validated against retained checkpoint/WAL versions; unexplained records inside the retained range are fatal, while obsolete pre-floor records may remain until physical compaction.

## Compaction recovery

Compaction uses copy-and-publish. If `.previous` exists without the canonical data file, open restores it. If both generations exist after process interruption, the published canonical replacement is structurally/checksum validated before the previous generation is retired; a torn or structurally corrupt replacement falls back to `.previous`. Main and branch recovery then validate the accepted physical state against authoritative checkpoint/WAL history before the database is exposed.
