# v0.5 MVCC model

ChronicleDB v0.5 uses immutable managed committed-version chains as the semantic source for current, transactional, snapshot, and time-travel reads.

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

The WAL is currently the durable source for reconstructing committed MVCC history. Recovery replays committed transactions in commit order to rebuild chains. Persistent snapshot metadata records a durable `RetentionFloor`; historical APIs reject boundaries below that floor.

When opening pre-MVCC physical state, ChronicleDB writes one durable synthetic bootstrap transaction to WAL for current keys that have no WAL-backed version. This assigns those keys a stable upgrade sequence exactly once; their apparent history cannot drift forward on later reopens.

v0.5 intentionally performs no aggressive committed-history reclamation. Deleting a named snapshot does not physically delete versions, which also keeps already-open snapshot handles safe.
