# ChronicleDB

ChronicleDB is an experimental embedded, persistent, versioned key-value storage engine for .NET 10. The v1.0 release architecture combines crash-safe MVCC and Snapshot Isolation with persistent historical roots, copy-on-write/shared-state branches, independent branch WAL/recovery, conservative branch lifecycle, retained-history garbage collection, and copy-and-publish physical compaction.

The current release deliberately does **not** claim branch merge/rebase, cross-history transactions, latch-free indexing, epoch-based reclamation, native-memory hot paths, distributed operation, group commit, or SQL. Those remain later-release work.

## v1.0 guarantees

- binary keys (including the zero-length binary key) use full structural identity and engine-owned bytes;
- acknowledged durable commits have a recoverable WAL decision before publication;
- multi-key transactions become logically visible as one committed unit;
- transactions read one fixed `StartSequence` plus their own writes;
- first-committer-wins prevents overlapping write/write commits;
- Snapshot Isolation is explicitly **not** serializable;
- persistent named snapshots survive restart and keep a fixed historical boundary;
- retained commit-sequence views are read-only and deterministic;
- snapshot deletion removes only that persistent root; GC later reclaims history only when no remaining root or active observer requires it;
- persistent snapshots are represented as generalized history roots and survive restart through `chronicle.history-roots`;
- interrupted root publication is reconciled deterministically during open;
- branch creation is metadata-oriented: inherited state remains shared through a fixed historical base rather than being copied;
- Main and each branch use distinct history domains and local commit-sequence namespaces;
- branch reads distinguish local values, local tombstones, and parent fallback through one resolver;
- branch snapshots and branch-local historical reads remain stable while Main, siblings, and the branch continue evolving;
- nested branching is correctness-first and bounded to 16 levels;
- every writable branch owns an identity-bound WAL and durable branch commits are redone after interrupted physical publication;
- branch deletion is crash-safe and is rejected while active handles, persistent branch snapshots, or child branches depend on the history;
- generic time-travel floors are distinct from explicit snapshot/branch roots, allowing per-key historical reclamation instead of one global minimum;
- GC publishes a complete retained-history checkpoint before rotating WAL or removing managed versions;
- physical compaction uses copy/fsync/validate/publish rather than destructive in-place rewriting and supports bounded rewrite budgets;
- lifecycle journals are compacted to canonical active state so bounded create/delete workloads do not leak metadata indefinitely;
- complete persistent corruption is rejected rather than silently repaired;
- only proven crash tails or derived state recoverable from authoritative history are repaired.
- retained-history checkpoint framing rejects physically impossible record counts before allocating record collections;
- WAL/checkpoint recovery revalidates configured logical key/value limits before replay or physical redo, while obsolete pre-checkpoint WAL generations remain structural evidence rather than replay input;
- history-topology diagnostics expose Main/branch ancestry, local sequence/floor, version depth, snapshots, data/WAL bytes, open readers, and explainable persistent retention roots;
- deleted branch-private directory cleanup is retryable physical reclamation and cannot fault a logically valid database solely because the host filesystem temporarily refuses deletion;
- deterministic v1.0 workload replay covers Main and multiple branches, snapshots, historical reads, restart, GC, and compaction with intermediate differential validation;
- the v1.0 research runner records reproducibility metadata and dedicated branch creation/read/write/scale, GC, compaction, and recovery measurements without claiming superiority from the runner itself.

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
- [History model](docs/architecture/HISTORY_MODEL.md)
- [History roots](docs/architecture/HISTORY_ROOTS.md)
- [Retention](docs/architecture/RETENTION.md)
- [Branch semantics](docs/architecture/BRANCHING.md)
- [Branch storage](docs/architecture/BRANCH_STORAGE.md)
- [Branch WAL](docs/architecture/BRANCH_WAL.md)
- [Branch recovery](docs/architecture/BRANCH_RECOVERY.md)
- [Version GC](docs/architecture/VERSION_GC.md)
- [Compaction](docs/architecture/COMPACTION.md)
- [History ownership](docs/architecture/HISTORY_OWNERSHIP.md)
- [Correctness invariants](docs/architecture/INVARIANTS.md)
- [Crash harness](docs/architecture/CRASH_HARNESS.md)
- [Testing methodology](docs/TESTING.md)
- [Benchmarking methodology](docs/BENCHMARKING.md)
- [Research evaluation contract](docs/RESEARCH_EVALUATION.md)

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
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- 1000 8 .artifacts/benchmarks/v10.json 42
```

The SDK is pinned by `global.json`. Package versions, compiler settings, analyzer policy, artifact paths, and the default unsafe-code prohibition are centralized at the repository root.
