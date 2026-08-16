# ChronicleDB

ChronicleDB is an embedded, persistent key-value storage engine for .NET 10. It stores binary keys and values and provides durable transactions, MVCC snapshots, writable copy-on-write branches, WAL recovery, retention-aware garbage collection, and compaction.

## What it provides

- page-based persistent storage with versioned binary formats and CRC32C corruption detection;
- atomic multi-key transactions backed by a write-ahead log;
- immutable committed MVCC versions with Snapshot Isolation and first-committer-wins conflicts;
- persistent named snapshots and historical reads;
- independently writable branches rooted in retained history;
- branch-local WAL, recovery, snapshots, historical reads, tombstones, and bounded nested branching;
- retention-aware version garbage collection;
- retained-history checkpoints and WAL rotation;
- copy-and-publish physical compaction with bounded maintenance budgets;
- replaceable version-index boundaries and fault-injection coverage.

Snapshot Isolation is **not** serializable. SQL, networking, replication, branch merge/rebase, cross-history transactions, distributed operation, native-memory hot paths, and latch-free indexes are outside the current product scope.

## Durability

An acknowledged durable commit has a recoverable WAL decision before it is reported as successful. Recovery may redo derived physical state, but it must never invent a commit or lose an acknowledged one.

Checksums detect corruption; they do not provide cryptographic authenticity. See [Security](docs/SECURITY.md) for the integrity limits and operational responsibilities.

## Repository guide

- [Architecture](ARCHITECTURE.md) — assembly ownership and dependency rules.
- [Storage format](docs/architecture/STORAGE_FORMAT.md) and [WAL format](docs/architecture/WAL_FORMAT.md) — persistent byte contracts.
- [Transactions](docs/architecture/TRANSACTIONS.md), [MVCC](docs/architecture/MVCC.md), and [Isolation](docs/architecture/ISOLATION.md) — transactional semantics.
- [Recovery](docs/architecture/RECOVERY.md) — main and branch recovery authority.
- [Snapshots](docs/architecture/SNAPSHOTS.md), [History roots](docs/architecture/HISTORY_ROOTS.md), and [Retention](docs/architecture/RETENTION.md) — historical-state lifecycle.
- [Branching](docs/architecture/BRANCHING.md), [Branch storage](docs/architecture/BRANCH_STORAGE.md), and [Branch recovery](docs/architecture/BRANCH_RECOVERY.md) — writable historical branches.
- [Version GC](docs/architecture/VERSION_GC.md) and [Compaction](docs/architecture/COMPACTION.md) — maintenance and reclamation.
- [Security](docs/SECURITY.md), [Testing](docs/TESTING.md), and [Benchmarking](docs/BENCHMARKING.md) — operational guidance and validation.

## Build and test

The repository pins its SDK in `global.json`.

```powershell
dotnet restore ChronicleDB.slnx --ignore-failed-sources
dotnet build ChronicleDB.slnx -c Release --no-restore
dotnet test ChronicleDB.slnx -c Release --no-build
```

Run the deterministic workload runner:

```powershell
dotnet run -c Release --project tools/ChronicleDB.WorkloadRunner -- 42 10000 8
```

Run the process-level crash harness:

```powershell
dotnet run -c Release --project tools/ChronicleDB.CrashHarness -- run 100
```

Generate a local benchmark report:

```powershell
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- 1000 8 .artifacts/benchmarks/v10.json 42
```

Benchmark output describes one build and workload; it is not a claim of superiority over mature database systems.
