# ChronicleDB

ChronicleDB is an experimental embedded key-value storage engine for .NET 10. It stores binary keys and values, provides WAL-backed transactions with Snapshot Isolation, retains historical MVCC state for persistent snapshots and time-travel reads, and can create independently writable branches from retained history without copying the complete logical database.

The v1.0 line is the semantic baseline for later performance research. Correctness, crash recovery, history retention, and branch isolation are intentionally prioritized over latch-free execution or peak throughput.

## What v1.0 provides

- page-based persistent storage with explicit versioned binary formats and CRC32C corruption detection;
- atomic multi-key transactions backed by a write-ahead log;
- immutable committed MVCC versions and Snapshot Isolation with first-committer-wins write conflicts;
- persistent named snapshots and commit-sequence historical reads;
- explicit history domains with per-history commit-sequence namespaces;
- metadata-oriented branch creation from a fixed parent history boundary;
- branch-local WAL, recovery, snapshots, historical reads, tombstones, and bounded nested branching;
- generalized history roots for snapshots and branch bases;
- retention-aware version garbage collection;
- retained-history checkpoints that allow WAL rotation without losing reconstructable history;
- copy-and-publish physical compaction with bounded maintenance budgets;
- deterministic differential workloads, fault injection, process-level crash testing, topology diagnostics, and machine-readable benchmarks.

Snapshot Isolation is **not** serializable. Branch merge/rebase, cross-history transactions, replication, distributed operation, SQL, native-memory hot paths, epoch-based reclamation, and the planned latch-free index are outside v1.0.

## Core durability rule

An acknowledged durable commit has a recoverable WAL decision before it becomes an acknowledged success. Physical publication and logical transaction visibility are distinct concepts: recovery may redo derived physical state, but it must never invent a commit or lose an acknowledged one.

For retained history, the recovery authority is the latest validated history checkpoint plus the authoritative WAL generation that follows it. Checksums detect corruption; they do not provide cryptographic authenticity. See [Security](docs/SECURITY.md).

## Repository guide

- [Project definition](project-definition.md) — product scope, semantic contract, invariants, and v1.5 transition boundary.
- [Architecture](ARCHITECTURE.md) — assembly ownership and dependency rules.
- [Architecture decisions](docs/adr/README.md) — durable design decisions and compatibility history.
- [Storage format](docs/architecture/STORAGE_FORMAT.md) and [WAL format](docs/architecture/WAL_FORMAT.md) — persistent byte contracts.
- [Transactions](docs/architecture/TRANSACTIONS.md), [MVCC](docs/architecture/MVCC.md), and [Isolation](docs/architecture/ISOLATION.md) — transactional semantics.
- [Recovery](docs/architecture/RECOVERY.md) — Main and branch recovery authority.
- [Snapshots](docs/architecture/SNAPSHOTS.md), [History roots](docs/architecture/HISTORY_ROOTS.md), and [Retention](docs/architecture/RETENTION.md) — historical-state lifecycle.
- [Branching](docs/architecture/BRANCHING.md), [Branch storage](docs/architecture/BRANCH_STORAGE.md), and [Branch recovery](docs/architecture/BRANCH_RECOVERY.md) — writable historical branches.
- [Version GC](docs/architecture/VERSION_GC.md) and [Compaction](docs/architecture/COMPACTION.md) — maintenance and reclamation.
- [Security](docs/SECURITY.md) — threat model, integrity limits, and operational responsibilities.
- [Testing](docs/TESTING.md), [Benchmarking](docs/BENCHMARKING.md), and [Research evaluation](docs/RESEARCH_EVALUATION.md) — release evidence and experiment discipline.

## Build and validate

The repository pins its SDK in `global.json`.

```powershell
dotnet restore ChronicleDB.slnx
dotnet build ChronicleDB.slnx -c Release --no-restore
dotnet test ChronicleDB.slnx -c Release --no-build
```

Run deterministic multi-history validation:

```powershell
dotnet run -c Release --project tools/ChronicleDB.WorkloadRunner -- 42 10000 8
```

Run process-level crash injection:

```powershell
dotnet run -c Release --project tools/ChronicleDB.CrashHarness -- run 100
```

Collect a machine-readable benchmark report:

```powershell
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- 1000 8 .artifacts/benchmarks/v10.json 42
```

Benchmark output is evidence about a particular build and workload, not a claim of superiority over mature database systems. Historical v0.5 comparisons must be run from the actual v0.5 revision rather than simulated inside the v1.0 binary.
