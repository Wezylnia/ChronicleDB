# ADR 0008: v0.4 conventional concurrent MVCC baseline

## Status

Accepted.

## Context

v0.3 used correct MVCC semantics but broad facade serialization would make readers wait behind WAL durability work. v0.5 also needs a baseline that remains understandable enough to validate later latch-free research.

## Decision

Use conventional managed synchronization:

- transaction objects own their own state gates;
- immutable committed version chains are protected by reader/writer synchronization;
- the baseline full-key version index permits parallel readers and exclusive publication;
- ordinary current/historical/snapshot reads and transaction construction do not acquire the commit gate;
- one commit coordinator still serializes conflict validation, sequence allocation, WAL LSN order, durability barrier, append-only physical publication order, and final version publication.

The coordinator is deliberately retained because the current single WAL and append-only storage encode one ordered durable history. Removing it before a more precise reservation/publication protocol would add races without proving a benefit.

## Consequences

Multiple readers execute concurrently and writers can perform transaction-local work concurrently. Durable commits queue at the ordered coordinator. Diagnostics expose commit and index contention so later work can measure whether replacing the coordinator/index is justified.

This ADR makes no lock-free progress claim.
