# v1.0 branching semantics

ChronicleDB v1.0 represents a branch as an independently writable `HistoryId` rooted at a fixed retained boundary in one parent history. Branch creation shares immutable historical state; it does not copy the parent's logical dataset. New branch-owned writes are persisted separately and parent state is never modified by branch commits.

## Identity and sequence namespaces

Main and every writable branch have distinct `HistoryId` values. Commit sequences are local to a history domain, so a sequence is meaningful only as `(HistoryId, CommitSequence)`. Every branch also has a persistent `BranchId`, one immutable parent history, one fixed parent base sequence, a persistent base-retention root, and a bounded ancestry depth.

## Creation

Creating a branch publishes durable lifecycle state in this order:

1. validate the requested parent history/boundary and reserve branch/history/name identity;
2. persist a `CreateIntent` in `chronicle.branches`;
3. create the branch-private storage domain;
4. create a `BranchBase` history root that protects the selected parent boundary;
5. persist `Activate`, binding the branch to its private storage identity;
6. make the branch discoverable and acknowledge creation.

An interrupted create is reconciled during open. A branch is externally valid only after activation. Incomplete creation cannot leave an indefinitely retaining hidden root or a discoverable half-initialized branch.

## Read resolution

All branch reads use one resolver. For a key at branch-local boundary `S`:

1. transaction-local writes win for an active transaction;
2. resolve the newest branch-local committed version at or before `S`;
3. a local value is returned;
4. a local tombstone returns absence and **does not** fall back;
5. only `NoVisibleVersion` falls back recursively to the immutable parent history at the branch's fixed `ParentBaseSequence`.

This distinction is required for correct local deletion.

## Snapshot Isolation and local commits

A branch transaction captures the branch local current sequence as `StartSequence`. It reads that fixed local snapshot plus its own writes. First-committer-wins conflicts are validated only against commits in the same branch history. Main, siblings, and descendants evolve independently after a branch point.

Each branch has its own commit coordinator and `branch.wal`. A durable branch commit validates/preflights before logging, appends identity-bound Begin/mutations/Commit records, fsyncs the branch WAL, crosses the one-way durable decision, then publishes branch-private physical versions, lifecycle/cache metadata, and the in-memory MVCC view. Failure after WAL durability is recovery-defined rather than abortable.

## Historical reads and snapshots

A branch historical boundary is `(branch HistoryId, local sequence)`. Local history resolves at that sequence and unchanged keys fall back to the same immutable parent base. Persistent snapshots inside a branch retain a fixed local boundary and remain unchanged by later branch, parent, or sibling writes.

A branch created from a named snapshot acquires its own `BranchBase` root. Deleting the source snapshot therefore does not invalidate the branch, and deleting the branch does not invalidate an independent surviving snapshot.

## Nested branches

Nested branches are supported recursively for correctness. A child fixes the complete parent-visible state at a selected parent-local sequence. v1.0 enforces `ChronicleBranch.MaximumDepth` to bound recursive resolution; creation validates the limit and persistent ancestry validation rejects missing parents, self-parenting, inconsistent depth, and cycles.

## Lifecycle and deletion

Deletion is conservative. It is rejected while the branch has open branch handles, active transactions, open historical/snapshot handles, persistent branch snapshots, or child branches. A durable `DeleteIntent` closes the history to new operations; after dependency-safe root release, `DeleteComplete` publishes logical deletion. Physical branch-directory removal is later reclamation: transient filesystem cleanup failures are reported as pending and retried by later GC passes without reviving the branch or faulting otherwise valid logical state.

## Retention and maintenance

A branch base remains an explicit root even when the parent generic time-travel floor advances past the branch point. GC protects only the per-key parent versions needed to reconstruct that exact base, rather than pinning every unrelated intermediate parent version. Branch-local snapshots and process-local readers similarly protect exact local boundaries.

Before branch WAL history is rotated for GC or compaction, a complete checksummed retained-history checkpoint is made durable. Physical compaction rewrites only branch-private retained versions; inherited parent state remains shared.

## Compatibility note

v0.7 used `AdvanceSequence` metadata as a committed-prefix authority. On first v0.8+ open, a legacy branch is validated and deterministically bootstrapped into an independent branch WAL before the `WalInitialized` capability becomes durable. In v1.0, checkpoint + branch WAL are transaction-history authority; `AdvanceSequence`/physical-boundary metadata are lifecycle and derived-storage publication state, not the commit decision.
