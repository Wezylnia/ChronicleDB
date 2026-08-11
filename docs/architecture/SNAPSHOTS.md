# Persistent Snapshots and Time Travel

ChronicleDB exposes retained MVCC history through persistent named snapshots and fixed-boundary historical views in Main and branch histories. Snapshot creation is metadata-oriented: it does not copy the visible database state.

## Persistent named snapshots

A Main snapshot contains:

- stable `SnapshotId`;
- owning `DatabaseId`;
- unique case-sensitive name;
- fixed Main commit-sequence boundary;
- creation timestamp.

A branch snapshot additionally belongs to one branch `HistoryId`; its sequence is interpreted only inside that branch's local commit namespace and its inherited parent state remains fixed by the branch base.

Creating a snapshot persists a lifecycle record and the corresponding history-root record before the handle is returned. The captured commit sequence is immutable: later Main, branch, or sibling commits cannot change the snapshot's logical view.

## Crash semantics

- failure before persistence begins has no durable snapshot effect;
- once a metadata write may have started, an uncertain outcome requires reopen/reconciliation rather than guessing;
- a complete durable Create remains authoritative even if client acknowledgement is lost;
- an incomplete framed tail is recoverable truncation, while complete corrupt metadata is rejected;
- Delete uses the same durable lifecycle discipline and reconciliation rules.

## Open handles and deletion

Deleting a named snapshot makes future opens by ID/name fail, but an already-open snapshot handle remains valid until it is disposed. Open snapshot and historical handles register process-local retention boundaries, so v1.0 GC cannot reclaim the versions they still observe even after the persistent name/root has been deleted.

This distinction is intentional:

- **persistent root lifetime** controls whether the snapshot can be reopened later;
- **open handle lifetime** controls whether an already acquired observer may continue reading in the current process.

## Point-in-time views

`OpenHistoricalView(sequence)` creates a read-only fixed-boundary handle in Main. Branches expose the equivalent branch-local historical view.

For generic time travel, the requested sequence must be within the history's retained range:

`RetentionFloor <= sequence <= CurrentSequence`

A read chooses the newest committed version visible at that sequence. Tombstones represent absence and, in branches, suppress inherited parent fallback.

A raw sequence is always scoped to one `HistoryId`; Main sequence 100 and Branch A sequence 100 are unrelated logical boundaries.

## Retention and GC

v1.0 separates the generic time-travel floor from explicit persistent roots. Maintenance may advance the generic floor and reclaim unreachable older versions, but it must preserve:

- every active persistent snapshot boundary;
- every branch base;
- every persistent branch snapshot;
- every active transaction start boundary;
- every open snapshot/historical handle;
- all recovery requirements.

The retained MVCC projection is durably checkpointed before WAL rotation or physical compaction. Consequently snapshot deletion can eventually release history, but only after no other durable or process-local observer requires the same logical state.

## Branch creation from snapshots

An open Main or branch snapshot can be used as a branch source. Branch creation establishes its own durable `BranchBase` root, so the resulting branch is independent of the source snapshot lifecycle. Deleting the source snapshot later does not move or invalidate the branch base.
