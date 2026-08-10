# v0.7 core branch semantics

ChronicleDB v0.7 turns retained historical state into independently writable history domains. Branching is correctness-first: branch-local commits use conservative synchronization and append-only local storage; the independent branch WAL and full crash/lifecycle protocol remain v0.8 work.

## Identity and sequence namespaces

Main and every writable branch have distinct `HistoryId` values. Commit sequences are local to a history domain, so a sequence is meaningful only as `(HistoryId, CommitSequence)`. Every branch also has a persistent `BranchId`, a stable parent history, and a fixed parent base sequence.

## Creation

Creating a branch publishes three durable facts in order:

1. a `CreateIntent` in `chronicle.branches` reserves branch/history/name identity;
2. an empty branch-local storage domain is created and a `BranchBase` history root retains the selected parent boundary;
3. an `Activate` record publishes the local storage identity and makes the branch recoverably active.

A branch never copies the parent's logical dataset during creation. If creation is interrupted before activation, open reconciliation deletes any orphaned base root and partially initialized branch-local directory before durably abandoning the creation intent. If activation may have become durable, the database is faulted and reopen is authoritative.

## Read resolution

All branch reads use one resolver. For a key at branch-local boundary `S`:

1. transaction-local writes win for an active transaction;
2. the newest branch-local committed version at or before `S` is resolved;
3. a local value is returned;
4. a local tombstone returns absence and **does not** fall back;
5. only `NoVisibleVersion` falls back recursively to the immutable parent history at the branch's fixed `ParentBaseSequence`.

This distinction is required to make local delete correct.

## Snapshot Isolation

A branch transaction records the branch's local current sequence as `StartSequence`. It reads that fixed local snapshot plus its own writes. Write/write conflicts are validated only against newer committed versions in the same writable history. Main, siblings, and descendants do not directly conflict after a branch point because they publish to different history domains.

Branch commits are serialized per branch in v0.7. Different branches do not share that commit gate. This is deliberately conventional synchronization; latch-free publication is not a v0.7 objective.

## Branch-local commit publication

v0.7 stores every branch-local logical version as a new append-only physical record. The branch metadata `AdvanceSequence` record publishes the committed local sequence and the exact branch-data prefix covered by that commit. On reopen, data beyond the latest published prefix is discarded and the local MVCC index is rebuilt from the published records.

`AdvanceSequence` is a v0.7 committed-prefix protocol, **not** the branch WAL promised by v0.8. v0.8 will introduce logically independent WAL streams and the full branch durability/recovery contract.

## Historical reads and snapshots

A branch historical view is identified by `(branch HistoryId, local sequence)`. Local history is resolved at that sequence and unchanged keys fall back to the same fixed parent base.

Persistent snapshots created inside a branch store a branch-local sequence and receive a `PersistentSnapshot` history root in that branch history. Later branch, parent, or sibling writes cannot change the snapshot.

## Nested branches

Nested branches are supported recursively for correctness. A child fixes the complete parent-visible state at a selected parent-local sequence. v0.7 enforces a maximum depth of 16 to prevent unbounded recursive lookup; the limit is validated during creation and covered by tests.

## Isolation invariants

- branch commits never modify Main;
- sibling branches never modify one another;
- later parent commits never move an existing branch base;
- a local tombstone never exposes the inherited parent value;
- a branch snapshot never drifts after later writes;
- source snapshot deletion never invalidates a branch that has its own branch-base root.
