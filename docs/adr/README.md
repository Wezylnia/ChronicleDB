# Architecture Decision Records

ADRs record decisions that are expensive to reverse or that affect correctness, compatibility, ownership, or dependency direction.

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](0001-modular-monolith-and-assembly-boundaries.md) | Modular monolith and assembly boundaries | Accepted |
| [0002](0002-replaceable-index-contract.md) | Replaceable index contract | Accepted |
| [0003](0003-persistent-format-governance.md) | Persistent format governance | Accepted |
| [0004](0004-unsafe-code-containment.md) | Unsafe/native code containment | Accepted |
| [0005](0005-v01-storage-format.md) | v0.1 persistent storage format | Accepted |
| [0006](0006-v02-wal-record-format.md) | v0.2 WAL record format | Accepted |
| [0007](0007-v03-mvcc-and-snapshot-isolation.md) | v0.3 MVCC and Snapshot Isolation | Accepted |
| [0008](0008-v04-concurrent-mvcc-baseline.md) | v0.4 conventional concurrent MVCC baseline | Accepted |
| [0009](0009-v05-persistent-snapshots.md) | v0.5 persistent snapshots and conservative retention | Accepted |
| [0010](0010-v05-persistence-lifecycle-hardening.md) | v0.5 persistence lifecycle and framing hardening | Accepted |
| [0011](0011-v06-generalized-history-roots.md) | v0.6 generalized durable history roots | Accepted |

Use a new ADR when changing the project graph, persistent formats, commit/durability semantics, memory ownership, public lifetime rules, or baseline/optimized implementation strategy.
