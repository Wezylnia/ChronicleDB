# ChronicleDB Architecture

## Status

This document defines assembly ownership, dependency direction, and the runtime composition model for ChronicleDB v1.0. Semantic behavior is defined in the architecture-topic documents under `docs/architecture`. Local internal planning and decision records, when available under `private-docs/`, provide additional history; persistent-format changes and other costly decisions require an ADR.

## Architectural model

ChronicleDB is a modular monolith: one embedded engine distribution, split into assemblies only where a boundary protects correctness, durability, replacement, ownership, or tooling isolation.

The design separates five concerns that are easy to blur in a storage engine:

1. **semantics** — what transactions, versions, snapshots, roots, and branches mean;
2. **persistence** — how durable bytes are encoded, validated, flushed, and recovered;
3. **orchestration** — how the public engine coordinates semantic and persistence components;
4. **maintenance** — how obsolete logical state and physical storage are reclaimed without changing observations;
5. **research infrastructure** — tests, crash injection, inspection, and benchmarks that observe the engine but do not become correctness dependencies.

## Global rules

- Full binary key bytes define identity. Hashes are never identity.
- Persistent bytes are parsed through explicit codecs; raw CLR object layouts are never persisted.
- The dependency graph is acyclic.
- `ChronicleDB` is the runtime composition root and public embedded API.
- Internal types are preferred unless a type is part of the public API or an intentional replacement seam.
- Persistent DTOs do not leak into the public API.
- Unsafe code is disabled globally in v1.0.
- Baseline implementations remain available when later optimized implementations are introduced.
- Diagnostics and tools are observational; the engine never depends on them for durability or retention decisions.
- A lower-level persistence project does not call upward into transaction or branch semantics.

## Repository layout

| Area | Projects / directories | Responsibility |
| --- | --- | --- |
| Public runtime | `src/ChronicleDB` | Embedded API and composition root |
| Foundation | `src/Foundation/ChronicleDB.Core` | Identifiers, binary keys, sequences, limits |
| Semantics | `src/Semantics/ChronicleDB.Mvcc`, `ChronicleDB.History` | Visibility, snapshots, roots, branch metadata model, retention |
| Persistence | `src/Persistence/ChronicleDB.Storage`, `ChronicleDB.Wal` | Files, pages, values, journals, checkpoints, WAL framing and scanning |
| Indexing | `src/Indexing/ChronicleDB.Indexing.Abstractions`, `ChronicleDB.Indexing.Baseline` | Replaceable version-index contract and managed baseline |
| Engine | `src/Engine/ChronicleDB.Transactions`, `ChronicleDB.Recovery`, `ChronicleDB.Maintenance` | Transactions, committed-version state, recovery, GC/compaction contracts |
| Observability | `src/Observability/ChronicleDB.Diagnostics` | Counters and diagnostic support |
| Verification | `tests/Unit`, `tests/Persistence`, `tests/Correctness`, `tests/Recovery`, `tests/Architecture` | Unit, persistence, differential, recovery, and dependency validation |
| Tools | `tools/ChronicleDB.CrashHarness`, `ChronicleDB.Inspector`, `ChronicleDB.WorkloadRunner` | Process-crash testing, inspection, deterministic workloads |
| Benchmarks | `benchmarks/ChronicleDB.Benchmarks` | Reproducible research measurements |

## Assembly ownership

### `ChronicleDB.Core`

Owns dependency-light shared primitives: strongly typed identifiers, binary-key ownership and equality, commit sequences, and small invariant-oriented helpers. It is not a miscellaneous utility project.

### `ChronicleDB.Mvcc`

Owns pure version-visibility rules. It has no file or WAL responsibility.

### `ChronicleDB.History`

Owns the logical model for snapshots, history roots, branch definitions/catalog state, history-domain identity, and retention requirements. It answers semantic questions such as which history a root protects; it does not write files itself.

### `ChronicleDB.Storage`

Owns durable database files, pages, record/overflow encoding, metadata journals, history checkpoints, CRC32C validation, storage limits, append-prefix recovery, and copy-and-publish data-file rewrite. It treats durable bytes as untrusted input and has no authority to decide whether a transaction is committed.

### `ChronicleDB.Wal`

Owns WAL file/header framing, record codecs, LSN continuity, append/flush behavior, branch WAL envelopes, and structural WAL validation. Higher layers interpret complete WAL records as transactions.

### `ChronicleDB.Indexing.Abstractions`

Owns the smallest stable version-index seam required by the MVCC engine. The contract exposes logical keys and version heads, not locks, pages, native pointers, epochs, or tree nodes.

### `ChronicleDB.Indexing.Baseline`

Owns the v1.0 managed synchronized index. It remains the semantic/performance baseline for v1.5 differential testing.

### `ChronicleDB.Transactions`

Owns transaction state, private write sets, committed version chains, conflict validation, replay-capacity checks, and version-store synchronization. It depends on the logical index abstraction rather than a concrete tree/hash implementation.

### `ChronicleDB.Recovery`

Owns interpretation of authoritative durable history: transaction grouping, commit-sequence validation, checkpoint/WAL generation rules, physical recovery bases, and deterministic replay decisions. It uses storage/WAL codecs rather than reimplementing formats.

### `ChronicleDB.Maintenance`

Owns public maintenance configuration/result contracts. Runtime orchestration remains in the composition root because a GC/compaction pass coordinates multiple histories and persistence authorities.

### `ChronicleDB.Diagnostics`

Owns counters and measurement support. Metrics can report behavior but cannot authorize a commit, reclaim a version, or select recovery state.

### `ChronicleDB`

Owns public handles and runtime orchestration:

- database open/close and lifecycle;
- Main transaction commit coordination;
- snapshot and historical-view handles;
- branch creation/open/delete and branch runtime composition;
- branch transaction durability coordination;
- history-root integration;
- recovery sequencing across the history graph;
- GC/compaction pass coordination;
- public diagnostics.

It is allowed to know concrete implementations because it is the composition root. Concrete details should not flow back into semantic contracts.

## Dependency direction

The intended dependency direction moves from foundation to semantics, then persistence/engine implementations, then recovery/maintenance/diagnostics, and finally the public `ChronicleDB` composition root. Tools, benchmarks, and tests depend on the product; production assemblies never depend on those surfaces.

The exact allowed project-reference graph is enforced by architecture tests. Those tests, rather than this prose summary, are authoritative for compile-time edges.

## Persistence authority versus semantic authority

A recurring architectural rule is that the component storing bytes is not automatically the component deciding their meaning.

Examples:

- `WalLog` validates framed records; recovery decides which complete transactions committed.
- the branch lifecycle journal stores identity/ancestry/publication metadata; branch WAL/checkpoint history is transaction authority after v0.8.
- history roots describe durable retention requirements; the version store decides which concrete versions satisfy those boundaries.
- physical data pages represent committed state; a retained-history checkpoint plus WAL can be the authority used to rebuild derived branch pages.

This separation prevents convenient storage metadata from accidentally becoming a second transaction protocol.

## History domains

Main is the root history domain. Every writable branch receives a distinct `HistoryId` and local commit-sequence namespace. A historical coordinate therefore includes both history identity and sequence.

Branches form an acyclic parent tree because v1.0 has no merge. Persistent snapshots reference a point in a history but do not create writable ancestry.

The branch-base root is owned by the child history while protecting the parent history at the fixed branch point. GC relies on that distinction.

## Concurrency model

v1.0 is intentionally not latch-free.

- immutable committed versions support stable reads;
- `CommittedVersionStore` protects chain/index publication with managed synchronization;
- Main has an ordered durability-critical commit coordinator;
- each branch has its own ordered commit coordinator;
- different histories do not share write/write conflict domains;
- lifecycle/maintenance operations use a history gate to prevent branch creation/deletion or reader-registration races while retention state changes;
- public transaction handles serialize `Commit`, `Abort`, and disposal transitions for the same handle.

The purpose of this baseline is predictable semantics. v1.5 may replace selected synchronization mechanisms only after profiling and ownership design.

## Recovery model

Open proceeds from roots of authority toward derived state:

1. validate Main metadata and physical format;
2. load the Main retained-history checkpoint when its capability is durable;
3. scan and interpret Main WAL;
4. reconstruct Main MVCC state and reconcile physical publication;
5. validate persistent snapshot/root/branch metadata;
6. validate branch ancestry in dependency order;
7. for each branch, load checkpoint, interpret identity-bound branch WAL, rebuild MVCC, and validate/rebuild derived physical branch state;
8. expose the database only after all required histories pass validation.

No history is exposed while its parent/base dependency remains unresolved.

## Maintenance model

GC and compaction are separate operations.

**GC** computes the retained logical projection, publishes a complete history checkpoint, rotates WAL only after that checkpoint is durable, compacts managed version chains, advances generic floors, and canonicalizes lifecycle journals.

**Compaction** first refreshes recovery authority for the selected history, then builds and validates a replacement physical representation before atomically publishing it. A validated primary is never rolled back merely because stale backup cleanup fails.

v1.0 budgets compaction across histories. The selected history is still rewritten as a complete surviving physical state; page/segment-granular incremental rewrite is a future optimization.

## Persistent-format governance

A durable format owner must define:

- magic/version fields;
- exact integer widths and byte order;
- reserved fields;
- length/count limits;
- checksum scope;
- identity binding;
- crash-tail policy;
- corruption behavior;
- compatibility/migration rules;
- golden or corruption tests.

Complete corruption is fatal. Only a tail whose framing proves it may be incomplete is eligible for truncation. Derived state may be rebuilt only from independently validated authoritative history.

## Unsafe/native-code boundary

v1.0 compiles with unsafe code disabled. Planned native-memory work belongs in a dedicated future assembly after ownership has been specified for allocation, publication, reader protection, retirement, and reclamation.

Semantic or public assemblies must not expose raw pointers or depend on epoch internals.

## Tooling boundary

CrashHarness, Inspector, WorkloadRunner, benchmarks, and test projects are clients of supported public/internal test seams. They do not implement alternate WAL, MVCC, branch-resolution, or retention rules.

v1.1 research observation uses a minimal optional seam owned by `ChronicleDB.Diagnostics`. `IResearchEventSink`, `ObservationEnvelope`, manifests, and trace serializers are observational artifacts; the engine never consults them for durability, retention, recovery authority, or publication decisions. `MetricsMode` and `TraceMode` are selected by the `ChronicleDB` composition root. Research tools remain downstream clients and production assemblies do not depend on research runners or artifact storage.

The Inspector escapes control characters in persisted names before writing terminal output. Benchmark and diagnostic output is observational evidence and cannot affect engine state.

## Change policy

The following changes require an ADR plus corresponding architecture/format tests where relevant:

- persistent byte-layout changes;
- commit/durability ordering changes;
- recovery-authority changes;
- public lifetime/ownership changes;
- history/branch semantic changes;
- unsafe-code opt-in;
- dependency-direction changes;
- replacement of a baseline implementation with a new mandatory mechanism.

Performance refactoring that preserves these contracts still requires differential, recovery, and benchmark evidence before it becomes part of the v1 semantic baseline.
