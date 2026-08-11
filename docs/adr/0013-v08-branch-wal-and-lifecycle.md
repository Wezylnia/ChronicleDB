# ADR 0013: v0.8 branch WAL and lifecycle authority

## Status

Accepted.

## Decision

Every writable branch owns a logically independent `branch.wal`. Common WAL framing is reused, while every payload is additionally bound to `BranchId` and `HistoryId`. WAL fsync is the durable branch-commit decision. Branch metadata remains the persistent source for branch identity, ancestry, lifecycle, current-sequence cache, and physical publication boundaries, but it is not transaction commit authority.

Branch deletion is conservative: open handles, active transactions, persistent branch snapshots, and child branches block deletion. Delete intent and completion are persistent lifecycle events; branch-private file reclamation is deferred to v0.9.

## Consequences

- a durable branch commit can be redone when physical publication is interrupted;
- Main and branch WAL streams cannot be cross-replayed silently;
- v0.7 branches require a one-time crash-safe WAL bootstrap;
- branch recovery is ordered after validation of the parent historical base.
