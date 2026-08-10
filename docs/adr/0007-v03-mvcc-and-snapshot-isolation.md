# ADR 0007: v0.3 MVCC and Snapshot Isolation

- Status: Accepted
- Date: 2026-08-10

## Context

v0.2 established durable atomic transactions, but reads always observed the latest committed storage state. v0.3 requires stable transaction snapshots, immutable committed versions, first-committer-wins write conflicts, and recovery of the same logical version history after restart.

The v0.3 implementation must remain understandable enough to serve as the semantic baseline for the more concurrent v0.4 publication path.

## Decision

Each transaction captures the database's current `CommitSequence` when it begins. Reads first resolve transaction-local writes and then traverse the committed version chain for the key, selecting the newest committed version whose sequence is not newer than the transaction's start sequence.

Committed versions are immutable managed records. `IVersionIndex` identifies the current head of each full binary key's chain; the managed `CommittedVersionStore` owns version records and applies the centralized `VersionVisibility` rule. Deletes create tombstone versions rather than removing historical visibility.

Commits remain serialized by the database gate in v0.3. Before WAL logging, the engine checks every written key. If its newest committed version is newer than the transaction's start sequence, the commit aborts with `TransactionConflictException`. This implements first-committer-wins write/write conflict handling without introducing the v0.4 CAS/descriptor protocol prematurely.

The v0.3 WAL record envelope is raised to 65 MiB so the existing 64 MiB mutation-value limit remains representable together with maximum-size key/length metadata.

Every successful transaction receives a monotonically increasing commit sequence. The Commit WAL payload stores that sequence and the data-file length observed before physical publication. Recovery accepts legacy v0.2 empty Commit payloads, reconstructs commit sequences in log order for them, rebuilds the managed version chains from committed WAL history, and resumes sequence allocation from the recovered maximum.

## Consequences

- Transactions receive stable Snapshot Isolation reads.
- Write skew remains possible because v0.3 is not Serializable Isolation.
- Current reads and transaction reads use the MVCC version store rather than the mutable physical current-state index.
- WAL history is required to rebuild in-memory version chains until a later persistent MVCC/checkpoint representation is introduced.
- v0.3 commit publication is logically atomic because the database gate excludes readers during physical/index publication; v0.4 may replace this with finer-grained publication while preserving the same semantics.
