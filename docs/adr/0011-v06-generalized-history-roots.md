# ADR 0011: v0.6 generalized durable history roots

## Status

Accepted

## Context

v0.5 persisted named snapshots, but retention semantics lived partly in the snapshot catalog and partly in the snapshot metadata file. Branch bases, active transactions, and future reclamation would otherwise introduce competing retention rules.

## Decision

Introduce `HistoryId` and `HistoryRootId` in Core and define immutable root descriptors plus a thread-safe `HistoryRootRegistry` in `ChronicleDB.History`. Persist root lifecycle in a separate database-bound `chronicle.history-roots` stream owned by Storage. The stream uses fixed-size checksummed records and a monotonic event sequence. `ChronicleDB` reconciles snapshot metadata and root metadata during open and publishes a monotonic database capability flag only after successful initialization.

The durable protocol publishes complete Active or Deleted outcomes. Creating and Deleting are semantic in-flight states that retain history conservatively; uncertain operations are resolved by reopen and reconciliation.

## Compatibility

Existing v0.5 databases have no history-root capability flag. The first v0.6 open creates the root file, bootstraps roots from active snapshot records, and then appends the new metadata flag. Existing snapshot IDs become stable root IDs without changing `chronicle.snapshots` bytes. If the new flag is present but the root file is missing, open fails as corruption.

## Consequences

- Snapshot retention has one explainable semantic authority.
- A future branch base can use the same root registry and durable protocol.
- Root creation/deletion adds one metadata flush and a bounded reconciliation step.
- Physical GC is intentionally deferred; v0.6 only makes liveness requirements durable and queryable.
