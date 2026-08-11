# ChronicleDB Project Definition

## Status

This document defines the product and semantic boundary of ChronicleDB v1.0. Persistent-format details belong to the format documents, implementation ownership belongs to `ARCHITECTURE.md`, and irreversible design choices are recorded in ADRs.

v1.0 is the semantic baseline for the later v1.5 performance-research line. Optimizations may replace internal mechanisms, but they may not weaken the behavior defined here without an explicit compatibility decision.

## 1. Product definition

ChronicleDB is a single-node, embedded, persistent key-value storage engine written in C#/.NET. Its public data model is binary key to binary value. The engine combines:

- page-based durable storage;
- write-ahead logging and deterministic crash recovery;
- MVCC with Snapshot Isolation;
- persistent historical snapshots and point-in-time reads;
- independently writable database branches rooted in retained historical state;
- branch-aware retention, version garbage collection, and physical compaction.

The distinguishing design choice is that committed MVCC history is not treated only as short-lived concurrency metadata. Retained history is a persistent resource that can back read-only snapshots, time-travel views, and writable branch bases.

## 2. Scope and non-goals

ChronicleDB v1.0 is an embedded storage engine, not a relational database or distributed service. The following are outside the release contract:

- SQL parsing, query planning, relational operators, or ORM behavior;
- networking, replication, sharding, consensus, or distributed transactions;
- branch merge, rebase, automatic cross-history conflict resolution, or parent-tracking branches;
- transactions that atomically modify more than one history domain;
- Serializable Snapshot Isolation;
- latch-free/Bw-tree-inspired indexing, native-memory hot paths, or epoch-based reclamation;
- zero-copy public API guarantees.

These exclusions are deliberate. They keep the v1.0 semantic baseline small enough to reason about before the v1.5 optimization work begins.

## 3. Public data model

Keys and values are arbitrary binary sequences within configured limits. ChronicleDB owns its internal key and value representation; caller-owned mutable buffers do not become engine state by reference.

Full key bytes define identity. A hash is an indexing aid only. Hash collisions never merge distinct keys.

The storage layer explicitly supports empty values and the zero-length binary key. Limits are validated before durable publication and are revalidated when durable history is recovered. A persistent framing format may have a wider absolute envelope than the database configuration; that wider envelope does not override the logical database limits.

## 4. Transaction model

A transaction owns a private write set until commit. Reads resolve transaction-local writes first, then committed MVCC state visible at the transaction's fixed `StartSequence`.

ChronicleDB provides Snapshot Isolation:

- a transaction does not observe commits newer than its start sequence;
- it observes its own latest local write or delete;
- same-history write/write conflicts use first-committer-wins semantics;
- disjoint writes may exhibit write skew, because Snapshot Isolation is not serializable.

Every transaction belongs to exactly one writable history domain. Main and each branch therefore have independent conflict domains after a branch point.

## 5. Logical atomicity

Physical installation is not the transaction commit decision. A multi-key transaction may prepare or publish several physical records, but ordinary readers consider its writes committed only according to the authoritative transaction commit state.

The engine must never expose a partially committed multi-key transaction. Recovery must either reconstruct the complete committed transaction or treat it as incomplete.

## 6. Commit ordering and durability

The durable commit protocol preserves this semantic order:

1. validate transaction state and deterministic input limits;
2. validate write/write conflicts in the target history;
3. reserve the target history's commit sequence;
4. prepare the transaction's logical versions;
5. append the required WAL records, including Commit;
6. cross the configured stable-storage barrier;
7. publish committed logical state and derived physical state;
8. acknowledge success.

Once the WAL durability barrier has established the commit decision, a later failure is not an abort. The database enters a recovery-required state when the in-process outcome is ambiguous; reopen determines the durable result.

## 7. MVCC history

Committed logical versions are immutable. A version records the key, transaction identity, commit sequence, value or tombstone, and its predecessor within the history domain.

Visibility selects the newest eligible committed version at or before the requested sequence. Deletes are represented by tombstone versions, so an older historical observer can still see the value that preceded a deletion.

Sequence values are local to a history domain. A historical coordinate is therefore `(HistoryId, CommitSequence)`, not a sequence number by itself.

## 8. Persistent snapshots and time travel

A named snapshot is a durable read-only root at a fixed history boundary. Creating a snapshot publishes metadata; it does not copy the complete visible database.

A valid snapshot remains stable across future writes and restart. Deleting a snapshot removes that root's independent retention requirement; it does not immediately authorize physical deletion of every version the snapshot once referenced.

Generic time-travel reads are allowed only within the retained range for the selected history. Open process-local historical handles participate in retention so maintenance cannot reclaim state they may still read.

## 9. History roots

Persistent snapshots and branch bases use one generalized history-root model. A root records identity, owning database, owner history, protected history, boundary, kind, lifecycle state, and creation metadata.

For a branch base, the owner is the child history while the protected history is the parent. This distinction is essential: the child branch depends on a precise historical point in its parent.

The root registry is the authoritative persistent source for long-lived retention requirements. Active transactions and open historical handles add temporary process-local retention boundaries.

## 10. Branch model

A branch is an independently writable history rooted at one immutable parent boundary. Branch creation is metadata-oriented:

- the parent dataset is not copied;
- inherited state remains shared through the parent historical base;
- branch-local writes create branch-owned persistent versions;
- later parent commits are invisible to the existing branch base.

Every branch has a persistent `BranchId`, its own `HistoryId`, a fixed parent `HistoryId` and base sequence, a persistent branch-base root, branch-private storage, and an identity-bound branch WAL.

## 11. Branch read semantics

Branch lookup has one authoritative resolution rule:

1. transaction-local mutation, when applicable;
2. newest visible branch-local committed version;
3. local value returns that value;
4. local tombstone returns absence;
5. only the absence of a visible local version falls back to the fixed parent base.

A local delete must never reveal the inherited parent value again.

Nested branches follow the same rule recursively. v1.0 enforces a finite depth limit and validates ancestry as an acyclic parent graph.

## 12. Branch lifecycle

Branch creation is externally atomic: recovery exposes either a complete active branch or no active branch. A source snapshot and a branch created from it become independent roots; deleting either one does not invalidate the other.

Branch deletion is conservative. It is rejected while active transactions, open branch/history handles, persistent branch snapshots, or child branches depend on the history. Durable deletion removes logical accessibility first. Branch-private file removal is later reclamation and may be retried without reviving the branch.

## 13. Recovery authority

For each history, retained logical recovery authority is:

- the latest validated retained-history checkpoint, if initialized; plus
- the authoritative WAL generation after that checkpoint.

The physical Main data file and branch-private data files are validated representations of committed state. Branch-private physical state can be rebuilt from already validated branch checkpoint/WAL history when the storage format explicitly classifies it as derived.

Recovery validates identities, checksums, framing, transaction structure, commit-sequence ordering, configured logical key/value limits, branch ancestry, root dependencies, and physical publication boundaries before a history becomes usable.

A complete corrupted record is never silently reclassified as a crash tail. Repair is limited to proven incomplete append tails or explicitly derived state whose authoritative history is already known.

## 14. Retention and garbage collection

Logical obsolescence is necessary but not sufficient for reclamation. A version remains protected when it is required by current state, an active transaction, an open historical handle, a persistent snapshot, a branch base, a child-history dependency, a recovery checkpoint/WAL rule, or an in-progress maintenance operation.

v1.0 distinguishes:

- a generic per-history time-travel floor; and
- exact boundaries pinned by explicit roots and active readers.

This prevents one ancient branch base from automatically pinning every unrelated intermediate parent version. GC preserves all versions in the generic retained range and the exact per-key versions required to reconstruct explicit older boundaries.

Before GC rotates WAL or removes managed versions, it publishes a complete checksummed retained-history checkpoint. Reclamation must remain observationally invisible to every valid observer.

## 15. Physical compaction

GC decides what is logically reclaimable. Compaction decides how surviving physical state is rewritten.

v1.0 uses copy-and-publish compaction:

1. establish a fresh recovery checkpoint and WAL generation;
2. build a replacement physical representation separately;
3. fsync and validate the replacement;
4. publish the new generation through a recoverable rename protocol;
5. retain the previous generation until the new primary validates;
6. retire stale files as best-effort cleanup.

Maintenance is budgeted across histories. Within one selected history, the current v1.0 storage layout rewrites that history's complete surviving physical state. Finer page/segment incremental compaction is a future optimization; v1.0 does not claim it.

## 16. Persistent-format discipline

Every durable structure is an explicit binary protocol with fixed field widths, byte order, version fields, reserved-field validation, bounds, checksums, and documented corruption behavior. Persisted data never depends on CLR object layout or native pointer values.

Format parsers validate declared lengths and counts against absolute limits and the containing file before variable-size allocation. Recovery additionally applies the database's configured logical limits before admitting durable mutations into MVCC state or physical redo.

## 17. Security and trust boundary

ChronicleDB assumes the host application and operating system control access to the database directory. It does not provide encryption at rest, authentication, authorization, cryptographic signatures, or protection against an attacker who can arbitrarily replace storage files with newly forged valid CRCs.

CRC32C is used for accidental corruption detection, not authenticity. Process-randomized in-memory key hashing reduces exposure to predictable hash-flooding patterns, while structural equality remains authoritative.

Persistent inputs are treated as untrusted bytes for parsing purposes: lengths, counts, identities, checksums, sequence continuity, and semantic relationships are validated before use. See `docs/SECURITY.md` for the operational threat model.

## 18. Concurrency and performance baseline

v1.0 intentionally uses understandable managed synchronization where it protects semantic invariants. Main and each branch have ordered durability-critical commit coordination; readers use immutable MVCC state and read-side synchronization rather than a single database-wide read lock.

The baseline index remains replaceable behind `IVersionIndex`. v1.0 performance work may remove unnecessary allocations, repeated hashing, redundant serialization, or avoidable I/O, but it does not introduce latch-free/native lifetime complexity.

## 19. Diagnostics and observability

Diagnostics expose transaction counters, WAL activity, version-chain depth, persistent snapshot counts, branch topology, per-history sequence/floor information, local storage/WAL sizes, retention roots, GC activity, and compaction activity.

Diagnostics are observational. They are never durability or retention authority.

## 20. Validation strategy

Release evidence combines independent techniques:

- unit and format tests;
- corruption and boundary tests;
- MVCC property tests;
- reference-model differential testing;
- concurrent stress workloads;
- deterministic multi-history workload replay;
- recovery tests;
- separate-process crash injection;
- maintenance observational-equivalence tests;
- reproducible benchmarks with raw machine-readable output.

A successful process start is not sufficient recovery evidence. Reopen validation checks Main, every active branch, persistent snapshots, branch ancestry, retained historical observations, and metadata integrity.

## 21. Required invariants

The v1.0 release is governed by the following invariants:

- **Atomicity:** committed multi-key transactions are all-or-nothing logically.
- **Durability:** every acknowledged durable commit survives supported crashes.
- **No phantom commit:** incomplete transactions never recover as committed.
- **Snapshot stability:** future writes do not change an existing snapshot.
- **Historical determinism:** the same retained `(HistoryId, CommitSequence)` produces the same logical state.
- **Snapshot Isolation:** transactions do not observe future commits; documented SI anomalies remain possible.
- **Branch-base stability:** an active branch's inherited parent state never changes.
- **Parent and sibling isolation:** branch commits modify only their own history.
- **Tombstone correctness:** a branch-local delete continues to hide inherited state.
- **History-graph integrity:** branch ancestry is acyclic and identity-consistent.
- **Root retention:** no state required by a valid root or active observer is reclaimed.
- **GC equivalence:** GC changes storage cost, not logical observations.
- **Compaction equivalence:** compaction changes physical representation, not logical observations.
- **Persistent integrity:** malformed durable structures fail deterministically rather than being trusted.

## 22. Research evaluation

ChronicleDB is a research platform as well as an implementation exercise. Claims must be narrower than the measurements that support them.

The v1.0 benchmark suite measures current and historical reads, durable writes, snapshot creation, branch creation, inherited branch reads, branch-local writes, branch-count scaling, controlled branch storage amplification, snapshot retention amplification, GC, compaction, and recovery.

Results must retain commit hash, build configuration, runtime, machine details, seed, durability mode, and raw latency/allocation metrics. The actual v0.5 revision is the historical baseline; the v1.0 executable does not label a different code path as v0.5 for convenience.

## 23. v1.5 transition boundary

v1.5 may introduce profiling-driven native hot paths, epoch-based reclamation, and a Bw-tree-inspired latch-free index. Those mechanisms must preserve the v1.0 semantic and durability contract and remain differentially testable against the managed baseline.

Before unsafe/native optimization is accepted, ownership must be explicit: who allocates an object, who publishes it, who may read it, how it is retired, what protects readers, and when reclamation is legal.

The purpose of v1.5 is to replace selected mechanisms, not to redefine what a committed transaction, snapshot, historical view, or branch means.
