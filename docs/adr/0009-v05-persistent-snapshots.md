# ADR 0009: v0.5 persistent snapshots and conservative historical retention

## Status

Accepted.

## Context

v0.5 must expose MVCC history as durable named snapshots and commit-sequence time travel without prematurely introducing branch-aware GC or a new persistent version tree.

## Decision

Persist snapshot lifecycle in a separate database-bound, checksummed, append-only `chronicle.snapshots` protocol. A Create record stores stable ID, name, sequence, and timestamp; Delete records remove named roots. Use a durable retention floor in the snapshot-store header and keep committed historical versions conservatively.

The WAL remains the durable source for reconstructing retained version chains. Therefore v0.5 does not truncate historical WAL through checkpointing.

For databases upgraded from physical state whose earlier commit provenance cannot be proven, persist one synthetic WAL bootstrap transaction for those current keys and establish the first v0.5 retention floor at that stable upgrade boundary instead of inventing older history.

## Consequences

Snapshot creation is metadata-oriented rather than a full database copy. Snapshot deletion is cheap but does not immediately reclaim versions. Already-open handles remain valid. Disk/WAL growth is an explicit v0.5 trade-off and becomes input to the v1.0 retention/GC work.
