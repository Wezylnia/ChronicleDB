# ChronicleDB Architecture

## 1. Status and authority

This document is the architectural source of truth for the repository layout and compile-time dependency boundaries. The project definition and version plans remain authoritative for behavior, semantics, invariants, release gates, and research scope.

If an implementation needs a dependency forbidden here, first decide whether the responsibility is misplaced. If the dependency direction truly must change, record an ADR and update the architecture test in the same change.

## 2. Architectural objective

ChronicleDB is an embedded storage engine, not a web application and not a generic enterprise Clean Architecture solution. Its important boundaries are determined by:

- semantic correctness versus physical representation;
- volatile state versus durable state;
- logical history reclamation versus concurrent physical memory reclamation;
- stable contracts versus replaceable experimental implementations;
- safe managed code versus explicitly owned unsafe/native code;
- production engine behavior versus research, fault-injection, and benchmark infrastructure.

The architecture is a modular monolith: one repository and one embedded engine distribution, with multiple assemblies only where a compile-time boundary protects a real risk or replacement seam.

## 3. Design rules

1. Correctness semantics are independent of any one page, WAL, or index implementation.
2. The dependency graph is acyclic and points from orchestration toward stable lower-level contracts.
3. Only `ChronicleDB` is the runtime composition root and intended consumer entry point.
4. Source is `internal` by default. A type becomes public only because the embedded API or an intentional extension contract requires it.
5. Persistent DTOs are not domain objects and are not public API contracts.
6. Full binary keys define identity. Hashes never define equality by themselves.
7. No generic repository, generic service, global service locator, or miscellaneous `Common` project is introduced.
8. Assembly boundaries are not duplicated as nested `Controllers/Services/Repositories` layers. Each assembly uses cohesive feature folders.
9. Unsafe code is globally disabled. A future native-memory assembly must opt in explicitly and own every pointer lifetime.
10. Baseline implementations remain available for differential validation after optimized implementations are introduced.

## 4. Repository shape

```text
ChronicleDB.slnx
Directory.Build.props
Directory.Packages.props
global.json

src/
  ChronicleDB/                         Public facade and composition root
  Foundation/
    ChronicleDB.Core/                  Dependency-free identifiers and invariants
  Semantics/
    ChronicleDB.Mvcc/                  Version/visibility semantics
    ChronicleDB.History/               Roots, snapshots, branches, retention model
  Persistence/
    ChronicleDB.Storage/               Files, pages, allocation, values, checkpoints
    ChronicleDB.Wal/                   WAL format, append, flush, scan, validation
  Indexing/
    ChronicleDB.Indexing.Abstractions/ Replaceable logical index contract
    ChronicleDB.Indexing.Baseline/     Managed synchronized baseline index
  Engine/
    ChronicleDB.Transactions/          Transaction state and commit protocol
    ChronicleDB.Recovery/              Deterministic durable-state reconstruction
    ChronicleDB.Maintenance/           GC, reclamation planning, compaction scheduling
  Observability/
    ChronicleDB.Diagnostics/           Typed events, counters and invariant snapshots

tests/
  Architecture/                        Compile-time boundary contract
  Unit/                                Pure semantic and data-structure tests
  Persistence/                         Page, file, WAL and checkpoint tests
  Correctness/
    ChronicleDB.ReferenceModel/        Simple independent logical oracle
    ChronicleDB.CorrectnessTests/      Generated and differential workloads
  Recovery/                            Fault and crash-recovery matrix

tools/
  ChronicleDB.Inspector/               Read-only database/history/retention inspection
  ChronicleDB.CrashHarness/            Child-process crash orchestration
  ChronicleDB.WorkloadRunner/          Deterministic workload replay

benchmarks/
  ChronicleDB.Benchmarks/              Reproducible baseline and ablation runner

docs/
  adr/                                 Architectural decisions
  architecture/                        Supporting architecture analysis
```

Reserved for v1.2-v1.4, and created only when their release gate is reached:

```text
src/Concurrency/ChronicleDB.Memory.Native/
src/Concurrency/ChronicleDB.Reclamation.Epochs/
src/Indexing/ChronicleDB.Indexing.LatchFree/
```

These projects are deliberately not empty placeholders today. They are allowed only after ownership specifications, profiling evidence, and baseline tests exist.

### Mapping from the release plans

The plans use logical subsystem names; this document decides their physical assembly ownership:

| Planned subsystem | Physical owner |
| --- | --- |
| `ChronicleDB.Api` / Public Engine API | `ChronicleDB` |
| `ChronicleDB.Core` | `ChronicleDB.Core` |
| Transaction Manager | `ChronicleDB.Transactions` |
| MVCC Engine | `ChronicleDB.Mvcc` plus transaction orchestration |
| History Manager and Branch Manager | feature folders in `ChronicleDB.History` |
| Storage Manager and Page Manager | feature folders in `ChronicleDB.Storage` |
| WAL Manager | `ChronicleDB.Wal` |
| Recovery Manager | `ChronicleDB.Recovery` |
| Reclamation and Compaction | feature folders in `ChronicleDB.Maintenance` |
| Diagnostics Layer | `ChronicleDB.Diagnostics` |
| Baseline and optimized indexes | separate implementations of `ChronicleDB.Indexing.Abstractions` |

History and branching stay in one assembly because they share the history graph and retention invariants. Reclamation and compaction stay in one assembly because both consume the same liveness plan and physical publication workflow. They remain separate feature folders and may split later only under the criteria in section 13.

## 5. Production project ownership

### `ChronicleDB`

Owns the supported embedded API, configuration validation, engine open/close lifecycle, implementation selection, and composition. It may reference concrete modules because it is the composition root. Business and storage algorithms do not live here.

Expected feature folders:

```text
Configuration/
Database/
Transactions/
Snapshots/
Branches/
Errors/
```

### `ChronicleDB.Core`

Owns dependency-free identifiers, limits, invariant helpers, binary ownership vocabulary, clocks/sequences where they are truly universal, and errors shared by internal contracts. It must not become a dumping ground for helpers.

Candidates include `TransactionId`, `CommitSequence`, `LogSequenceNumber`, `PageId`, `HistoryId`, `SnapshotId`, and explicit owned/borrowed binary value types. Physical page structures do not belong here.

### `ChronicleDB.Mvcc`

Owns immutable version metadata, tombstone semantics, centralized visibility evaluation, version-chain traversal rules, and snapshot boundaries. It contains no file I/O, WAL writing, index implementation, timers, or background workers.

The visibility rule must have one authoritative implementation used by transactions, snapshots, historical reads, branches, recovery validation, and tests.

### `ChronicleDB.History`

Owns the generalized history graph: Main, persistent snapshot roots, branch roots, ancestry, branch-local shadowing, historical read boundaries, retention reasons, and lifecycle rules. It depends on MVCC semantics but not on transaction orchestration or persistence implementations.

Keeping snapshots and branches together prevents two competing retention models. Feature folders provide the internal boundary:

```text
Roots/
Snapshots/
HistoricalReads/
Branches/
Retention/
```

### `ChronicleDB.Storage`

Owns database files, page IDs and formats, checksums, allocation, overflow values, durable metadata slots, checkpoint storage, bounded parsing, and physical I/O abstractions. It does not decide transaction visibility, commit success, or history retention.

Expected feature folders:

```text
Files/
Pages/
Allocation/
Values/
Formats/
Checksums/
Checkpoints/
Faults/
```

### `ChronicleDB.Wal`

Owns WAL record framing and codecs, append, flush, durability boundaries, LSN management, recovery scanning, tail truncation detection, and log-specific fault injection. It does not publish logical commits or mutate the index.

WAL is separate from storage because its ordering and corruption rules have an independent format, failure matrix, and review surface.

### `ChronicleDB.Indexing.Abstractions`

Owns the smallest stable index seam required by the engine: collision-safe lookup of version heads, publication/update operations, capability reporting, and diagnostic snapshots. It must not expose locks, native pointers, delta-chain nodes, or a particular tree representation.

### `ChronicleDB.Indexing.Baseline`

Owns the understandable managed, synchronized index used as the initial implementation and permanent correctness/performance baseline. It depends on the index contract; transaction and recovery projects never reference it directly.

### `ChronicleDB.Transactions`

Owns transaction descriptors, states, local write sets, read-your-writes, conflict validation, commit sequence allocation, atomic publication, abort behavior, and the commit protocol that coordinates WAL, storage, index, MVCC, and history contracts.

This is orchestration code. It may coordinate dependencies but must not absorb their binary codecs, file operations, or concrete data structures.

### `ChronicleDB.Recovery`

Owns open-time recovery orchestration, checkpoint selection, valid WAL prefix replay, incomplete-transaction elimination, history reconstruction, index rebuilding, and recovered invariant validation. It reuses persistent codecs owned by Storage and WAL rather than defining copies.

Recovery is separate from ordinary transactions so startup-only failure handling does not pollute the foreground commit path.

### `ChronicleDB.Maintenance`

Owns retention analysis, logical version GC, page reclamation planning, copy-and-publish compaction, cleanup, throttling, and maintenance scheduling. It consumes immutable semantic snapshots and publishes physical changes through explicit storage/index contracts.

Logical version GC belongs here. Future epoch-based physical object reclamation does not; it receives its own concurrency assembly because its safety model is different.

### `ChronicleDB.Diagnostics`

Owns typed, low-allocation engine events, counters, diagnostic snapshots, and invariant observation contracts. It does not own business decisions and does not become a logging framework wrapper.

Diagnostics are observational. Disabling them may reduce visibility but must never change results, ordering, or durability.

## 6. Dependency graph

```text
ChronicleDB (public facade / composition root)
  -> Transactions
  -> Recovery
  -> Maintenance
  -> Indexing.Baseline
  -> all required stable contracts

Transactions
  -> Core + Diagnostics + Mvcc + History
  -> Storage + Wal + Indexing.Abstractions

Recovery
  -> Core + Diagnostics + Mvcc + History
  -> Storage + Wal + Indexing.Abstractions

Maintenance
  -> Core + Diagnostics + Mvcc + History
  -> Storage + Indexing.Abstractions

Indexing.Baseline
  -> Core + Diagnostics + Indexing.Abstractions

History -> Core + Mvcc
Storage -> Core + Diagnostics
Wal -> Core + Diagnostics
Diagnostics -> Core
Mvcc -> Core
Indexing.Abstractions -> Core
Core -> nothing
```

Key consequences:

- The transaction path sees only the index contract, never the baseline or future latch-free implementation.
- Recovery can rebuild replaceable structures without depending on the normal transaction manager.
- History cannot call persistence or transactions; persistence of history metadata is coordinated at a higher layer.
- Maintenance cannot use WAL accidentally. A maintenance operation requiring durable publication must expose that need to the composition/transaction boundary explicitly.
- The facade is the only source project allowed to choose concrete implementations.

The exact graph is executable policy in `ChronicleDB.ArchitectureTests`.

## 7. Folder rules inside a project

Folders represent cohesive features, not technical buckets. A feature keeps its model, algorithm, validation, and focused helpers together.

Preferred:

```text
ChronicleDB.Wal/
  Format/
    WalRecordHeader.cs
    WalRecordCodec.cs
    WalRecordValidator.cs
  Writing/
    WalWriter.cs
    FlushCoordinator.cs
  Reading/
    WalScanner.cs
    WalTailStatus.cs
```

Avoid:

```text
Models/
Interfaces/
Services/
Managers/
Helpers/
Utils/
```

An interface stays next to the capability that owns it unless it is the deliberate cross-implementation seam in `Indexing.Abstractions`.

## 8. Public API boundary

Consumers reference `ChronicleDB`, not internal subsystem projects. Public DTOs do not expose:

- physical page IDs unless explicitly documented as diagnostic handles;
- WAL positions as commit sequences;
- index nodes or hashes as key identity;
- pooled buffers whose lifetime cannot be guaranteed;
- native pointers or ref-like values across async/lifetime boundaries;
- persistence records used for on-disk compatibility.

CLR visibility and supported API are separate concerns in this multi-assembly design:

- public types in `ChronicleDB` are the supported consumer surface;
- `Indexing.Abstractions` is an intentional advanced extension surface;
- a cross-assembly type in another project is an engine SPI, not automatically a supported consumer API;
- engine SPI types use the narrowest possible contract and live under an ownership-specific namespace, never a generic `Shared` namespace;
- broad `InternalsVisibleTo` declarations are avoided because they erase the compile-time boundary the project split was created to protect.

The safe default read API returns owned data. A future borrowed/zero-copy API must be explicitly named, scoped, non-async across the borrow, and documented with mechanically enforced lifetime rules.

## 9. Persistent format governance

Page, database-header, checkpoint, WAL, snapshot, and branch metadata formats are protocols. Every format requires:

- magic and format version where applicable;
- fixed endianness and explicit field widths;
- documented limits and reserved bytes;
- checked arithmetic and complete bounds validation before interpretation;
- checksum scope and corruption behavior;
- forward/backward compatibility policy;
- golden byte fixtures;
- truncated, torn, oversized, and corrupted input tests;
- an ADR for incompatible changes.

Serialization code lives with the format owner. It is never duplicated in Engine, tools, or tests. Tests may provide independent decoders only when intentionally acting as an oracle.

## 10. Concurrency and async boundaries

- Conventional synchronization is the baseline until measurement justifies replacement.
- Publication points are explicit and documented; multi-key logical commit visibility must not emerge from incidental collection updates.
- Async I/O does not permit borrowed spans, locks, pins, or epoch guards to cross an `await` unless their contract explicitly proves safety.
- Background work receives immutable plans or stable handles, not mutable transaction objects.
- Logical commit sequence, LSN, and physical offsets remain distinct types.
- No component may claim the whole engine is lock-free because one implementation has a latch-free update path.

## 11. Evolution by release

### v0.1-v0.5

Implement managed storage, WAL, recovery, baseline indexing, transactions, MVCC, snapshots, conservative retention, diagnostics, reference-model validation, and crash tests in the existing projects.

### v0.6-v1.0

Expand `ChronicleDB.History` with generalized roots, branches, ancestry, and retention explanations. Expand `ChronicleDB.Maintenance` with reachability-aware GC and copy-and-publish compaction. Create a new assembly only if an actual dependency cycle, package boundary, or independent replacement need appears.

### v1.1-v1.5

After profiling and ownership gates:

1. Create `ChronicleDB.Memory.Native` for allocator, owned/borrowed native slices, debug guards, and leak tracking.
2. Create `ChronicleDB.Reclamation.Epochs` for worker registration, epoch guards, retirement queues, and safe physical reclamation.
3. Create `ChronicleDB.Indexing.LatchFree` as another implementation of `Indexing.Abstractions`.
4. Keep `Indexing.Baseline` and run identical workloads against both.
5. Let only `ChronicleDB` select an implementation from validated configuration.

Native and latch-free projects may depend inward on stable contracts. Existing semantic projects must not depend outward on their concrete types.

## 12. Testing architecture

| Test area | Purpose |
| --- | --- |
| `ArchitectureTests` | Project DAG, package ownership, unsafe-code boundary, central configuration |
| `UnitTests` | Pure visibility, state machines, codecs, data structures, boundary values |
| `PersistenceTests` | Real file I/O, pages, overflow values, WAL framing, checkpoints, corruption |
| `ReferenceModel` | Small, obvious, implementation-independent logical state model |
| `CorrectnessTests` | Property/generated workloads and differential replay |
| `RecoveryTests` | Child-process crashes, truncation/torn writes, reopen and durable-prefix validation |
| `CrashHarness` | Deterministic external process termination; not an in-process exception simulator |
| `WorkloadRunner` | Seeded workload record/replay shared by correctness, stress, and benchmarks |
| `Benchmarks` | Reproducible baselines and ablation; never a correctness oracle |

Test utilities are shared only through the narrow `ReferenceModel` project. A large universal `TestCommon` project is forbidden because it couples unrelated test suites and hides fixture ownership.

## 13. When to create or split a project

Create a project only when at least one condition is true:

- a concrete implementation must be replaceable behind a stable contract;
- unsafe/native code needs a hard containment boundary;
- an external package must not leak into semantic assemblies;
- startup/recovery code has materially different dependencies from foreground code;
- a separately runnable process or tool is required;
- architecture tests cannot otherwise enforce a critical dependency rule.

Do not split merely because a folder is large. Before splitting, prove the new dependency direction is acyclic and name the owner of cross-boundary contracts.

## 14. Forbidden shortcuts

- `ChronicleDB.Common`, `ChronicleDB.Helpers`, or a service-locator project.
- Transaction code referencing `Indexing.Baseline` or a future latch-free implementation.
- History code writing files or WAL records directly.
- Storage/WAL codecs copied into Recovery, Inspector, or tests.
- Public API returning mutable engine-owned buffers.
- `unsafe` enabled globally or in a semantic/persistence project.
- Benchmark-specific behavior in production paths without an explicit feature and equivalence tests.
- Circular project references resolved by moving unrelated types into Core.
- Empty future projects created only to make the tree look complete.

## 15. Change checklist

Before merging a feature:

1. Identify the owning module and semantic invariant.
2. State persistent-format and recovery impact.
3. Extend the reference model when logical behavior changes.
4. Add unit/property/differential tests before optimization.
5. Add fault injection when durable state changes.
6. Confirm the architecture test still represents the intended dependency graph.
7. Update an ADR for format, lifecycle, ownership, or dependency changes.
8. Benchmark only after correctness and recovery gates pass.
