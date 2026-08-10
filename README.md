# ChronicleDB

ChronicleDB is an experimental embedded, persistent, versioned key-value storage engine for .NET 10. The v0.5 baseline is intentionally correctness-first: it combines checksummed append-only storage, WAL-backed atomic transactions, MVCC Snapshot Isolation, concurrent transaction execution, persistent named snapshots, retained point-in-time reads, deterministic recovery, fault injection, differential testing, diagnostics, and reproducible baseline benchmarks.

The current release deliberately does **not** implement branching, latch-free indexing, epoch-based reclamation, native-memory hot paths, aggressive historical garbage collection, group commit, or SQL. Those belong to later release lines after the v0.5 semantics are treated as fixed.

## v0.5 guarantees

- binary keys use full structural identity and engine-owned bytes;
- acknowledged durable commits have a recoverable WAL decision before publication;
- multi-key transactions become logically visible as one committed unit;
- transactions read one fixed `StartSequence` plus their own writes;
- first-committer-wins prevents overlapping write/write commits;
- Snapshot Isolation is explicitly **not** serializable;
- persistent named snapshots survive restart and keep a fixed historical boundary;
- retained commit-sequence views are read-only and deterministic;
- snapshot deletion removes the named root but v0.5 conservatively keeps historical versions;
- complete persistent corruption is rejected rather than silently repaired;
- only proven crash tails are truncated or rebuilt.

## Start here

- [Project definition](project-definition.md)
- [Architecture](ARCHITECTURE.md)
- [Architecture decisions](docs/adr/README.md)
- [Storage format](docs/architecture/STORAGE_FORMAT.md)
- [WAL format](docs/architecture/WAL_FORMAT.md)
- [Transaction model](docs/architecture/TRANSACTIONS.md)
- [Commit protocol](docs/architecture/TRANSACTION_COMMIT.md)
- [Transaction state](docs/architecture/TRANSACTION_STATE.md)
- [MVCC](docs/architecture/MVCC.md)
- [Isolation contract](docs/architecture/ISOLATION.md)
- [Recovery](docs/architecture/RECOVERY.md)
- [Persistent snapshots and time travel](docs/architecture/SNAPSHOTS.md)
- [Correctness invariants](docs/architecture/INVARIANTS.md)
- [Crash harness](docs/architecture/CRASH_HARNESS.md)
- [Testing methodology](docs/TESTING.md)
- [Benchmarking methodology](docs/BENCHMARKING.md)

The detailed v0.5, v1.0, and v1.5 working plans may be kept outside the repository; the checked-in architecture documents are the implementation contract.

## Build and validate

```powershell
dotnet restore ChronicleDB.slnx
dotnet build ChronicleDB.slnx --no-restore
dotnet test ChronicleDB.slnx --no-build
```

Run deterministic workload replay:

```powershell
dotnet run --project tools/ChronicleDB.WorkloadRunner -- 42 1000 4
```

Run process-level crash injection (100 repetitions of every scenario):

```powershell
dotnet run --project tools/ChronicleDB.CrashHarness -- run 100
```

Run baseline measurements and retain raw JSON:

```powershell
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- 1000 8 .artifacts/benchmarks/v05.json
```

The SDK is pinned by `global.json`. Package versions, compiler settings, analyzer policy, artifact paths, and the default unsafe-code prohibition are centralized at the repository root.
