# ADR 0015: v1.0 Research-Grade Release Freeze

- **Status:** Accepted
- **Release:** v1.0

## Context

v0.6 through v0.9 established generalized history roots, writable copy-on-write branches, branch-local WAL/recovery, branch lifecycle, retained-history garbage collection, and copy-and-publish compaction. v1.0 is not an opportunity to add another major storage mechanism. Its purpose is to freeze those semantics and make their correctness, topology, cost, and recovery behavior observable and reproducible.

A research-grade release also needs stricter input hardening than a demo. Persistent metadata must reject impossible resource claims before allocation, best-effort physical cleanup must not poison logically valid history, and the tooling used for evaluation must observe the same authoritative state as the public engine.

## Decision

1. **Semantic freeze.** v1.0 preserves the v0.9 transaction, Snapshot Isolation, branch-base, WAL/checkpoint, retention, GC, and compaction semantics. Branch merge/rebase, cross-history transactions, latch-free indexing, EBR, and native-memory hot paths remain out of scope.
2. **History topology is observable.** `GetHistoryTopologyDiagnostics()` exposes Main and branch identity, ancestry, base/current/floor sequences, local version depth, snapshot counts, data/WAL bytes, process-local retention handles, and active persistent roots. Diagnostics are observational and never become correctness inputs.
3. **Persistent metadata is resource-bounded before allocation.** History-checkpoint record counts and declared record payloads must be physically possible for the containing file before variable-size allocations are attempted. The checkpoint/branch formats also preserve the engine-wide binary-key contract, including the valid zero-length key. Generic WAL/checkpoint framing limits are not treated as database policy: every authoritative recovered key/value is revalidated against the opened database's configured logical limits before MVCC replay or physical redo.
4. **Logical deletion is separated from opportunistic physical cleanup.** Failure to remove a branch-private directory after the branch is already durably deleted is reported as pending reclamation and retried by future GC; it does not fault an otherwise usable database.
5. **Release evidence is reproducible.** The workload runner exercises Main and multiple branches, persistent snapshots, historical reads, restart, GC, and compaction under a deterministic seed and performs intermediate differential checks. The benchmark runner records environment/workload metadata and includes branch creation, inherited/local branch access, branch scaling, GC, compaction, and recovery scenarios.
6. **An integrated release gate complements subsystem tests.** A complete history graph is driven through divergence, tombstones, nested branching, source-root deletion, GC, compaction, restart, and branch deletion while retained observers are checked throughout.

## Consequences

- v1.0 adds observability and evaluation surface without changing the logical database model.
- Benchmark labels describe measured operations rather than claiming asymptotic complexity or superiority over mature systems.
- A successful local `dotnet test`, process crash campaign, deterministic workload replay, and retained raw benchmark JSON are required release evidence; static review alone is insufficient.
- Future v1.5 optimizations can use the v1.0 topology, workload grammar, and metrics as a semantic/performance baseline.
