# v0.6 recovery

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
13. reconcile snapshot roots into the generalized registry after an interrupted v0.6 publication;
14. validate root boundaries against retained/current history;
15. expose the database as `Open`.

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

## Checkpoint policy

v0.5 does **not** truncate historical WAL through a checkpoint. The WAL is currently the durable input used to reconstruct retained MVCC chains; truncating it without first persisting equivalent historical versions would violate time travel and snapshot stability. Recovery time is benchmarked and recorded. A later checkpoint design must preserve retained history before it can become an optimization.
