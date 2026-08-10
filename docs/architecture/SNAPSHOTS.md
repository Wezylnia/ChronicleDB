# v0.6 persistent snapshots and time travel

v0.5 exposes retained MVCC history as read-only public state. Branching is not part of this release.

## Persistent named snapshot

A persistent snapshot contains:

- stable `SnapshotId`;
- owning `DatabaseId` through the snapshot-store header and public info;
- unique case-sensitive name;
- fixed commit-sequence boundary;
- creation timestamp.

Creating a snapshot does **not** copy database pages. The operation captures the current published commit sequence, persists one Create lifecycle record to `chronicle.snapshots`, persists the corresponding active history-root record to `chronicle.history-roots`, flushes both to the stable-storage boundary, registers the root and snapshot in memory, and only then returns the handle.

The capture point is the snapshot's linearization boundary. A transaction that commits after that sequence is excluded even if it completes before snapshot creation returns.

## Crash semantics

- failure before any snapshot record write: no durable effect; database can remain usable;
- after a write may have started but before durability: outcome is uncertain, snapshot metadata/database are faulted, reopen decides whether a complete record survived;
- after snapshot metadata flush: the root is durable even if acknowledgement is lost;
- a partial framed tail is discarded on reopen; complete corrupt metadata is rejected.

Delete follows the same durable lifecycle protocol with Delete records in both metadata streams. If a crash occurs between the two files, recovery reconciles the root registry with the authoritative snapshot catalog before exposing the database.

## Open and list

Snapshots can be listed and reopened by ID or name after restart. Names are valid nonblank UTF-8 text, have no leading/trailing whitespace, are case-sensitive, and are limited to 1,024 encoded bytes.

Deleting a named snapshot makes future opens by that ID/name fail. An already-open handle remains usable because v0.5 performs no physical historical reclamation.

## Point-in-time views

`OpenHistoricalView(sequence)` creates a read-only fixed-boundary handle. The sequence must satisfy:

`RetentionFloor <= sequence <= CurrentCommitSequence`

A historical read chooses the newest committed version at or before that boundary. Tombstones behave exactly as they do for transaction snapshots.

The API is database-scoped, so a raw sequence is interpreted only inside the database from which the view is opened. Persistent snapshot files themselves are explicitly bound to database identity.

## Retention

Fresh v0.5 databases start with retention floor zero and can time-travel through all WAL-reconstructable commits. When an older physical database lacks provable historical commit provenance, first v0.5 open establishes a conservative floor at the validated upgrade boundary rather than inventing history.

The floor remains conservative and is not advanced by snapshot deletion. v0.6 makes the retention roots queryable and durable; precise physical reclamation and branch-aware retention belong to later v1.0 stages.
