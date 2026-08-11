# v1.0 MVCC model

ChronicleDB v1.0 uses immutable managed committed-version chains as the semantic source for current, transactional, snapshot, branch, and time-travel reads. The managed implementation remains the correctness baseline for later v1.5 index/reclamation optimization.

## Commit sequences

`CommitSequence(0)` is the initial empty boundary. Every successful transaction, including a read-only committed transaction, receives one monotonically increasing sequence. Sequence gaps are semantically legal even though the current serialized commit coordinator normally uses the immediate successor.

A transaction captures the current committed sequence when it begins. That immutable value is its `StartSequence`.

## Version chains

Each final key mutation contributed by a committed transaction creates one logical version containing:

- full binary key identity;
- creator transaction identity;
- commit sequence;
- tombstone flag;
- immutable value bytes for non-tombstones;
- previous-version handle.

No committed older version is mutated to store an `EndSequence`. The baseline index only maps a full key to the newest version handle; MVCC remains authoritative for visibility.

## Visibility

`VersionVisibility` is the single committed-version predicate:

`version.State == Committed && version.CommitSequence <= visibilityBoundary`

Reads walk newest-to-oldest until they find the newest visible version. A visible tombstone means absence. Transaction-local writes are checked before the committed chain.

The same rule is used for:

- current reads, using the published current sequence;
- transaction reads, using `StartSequence`;
- named snapshot reads, using the snapshot's fixed sequence;
- explicit historical views, using the requested retained sequence.

## Atomic multi-key publication

`CommittedVersionStore` uses reader/writer synchronization. Parallel readers may traverse immutable chains. A committed multi-key write set is installed under one writer critical section, and the database current sequence is published only after the complete set is installed. Readers that captured the older sequence cannot see newer versions even if physical publication has already occurred.

This is the conventional correctness baseline for later optimized indexes; it is deliberately not latch-free.

## Recovery and retention

Recovery reconstructs retained committed MVCC history from the latest valid retained-history checkpoint plus the newer WAL generation. Before a checkpoint exists, WAL history is the durable source. Each history domain has a monotonic generic retention floor; explicit roots and already-open process observers may retain exact older boundaries independently of that generic floor.

When opening pre-MVCC physical state, ChronicleDB writes one durable synthetic bootstrap transaction to WAL for current keys that have no WAL-backed version. This assigns those keys a stable upgrade sequence exactly once; their apparent history cannot drift forward on later reopens.

v1.0 GC performs per-key retention analysis. It preserves versions required by the generic retained range, explicit snapshot/branch-base roots, already-open process observers, and latest current state. Deleting a named snapshot removes one persistent root but does not invalidate an already-open handle; physical reclamation is separated from logical root deletion.
