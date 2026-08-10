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

Use a new ADR when changing the project graph, persistent formats, commit/durability semantics, memory ownership, public lifetime rules, or baseline/optimized implementation strategy.
