# v0.3 MVCC model

ChronicleDB v0.3 uses immutable committed version chains to provide stable transaction snapshots.

## Commit sequences

`CommitSequence(0)` represents the initial empty history boundary. Every successful transaction receives a non-zero sequence greater than the previous committed sequence. Gaps are allowed by the semantic model, although the current serialized v0.3 commit path normally allocates the immediate successor.

A transaction captures the current committed sequence when it begins. That value is its immutable `StartSequence`.

## Version chains

Each committed key mutation creates one logical version containing:

- full binary key identity;
- creator transaction identity;
- commit sequence;
- tombstone flag;
- immutable value bytes when not a tombstone;
- previous-version handle.

The baseline index maps each full binary key to the newest version handle. The index does not decide visibility; it only locates the chain head.

## Visibility

Transaction-local writes are checked first. Otherwise the engine walks newest-to-oldest and selects the first version for which:

`version.State == Committed && version.CommitSequence <= transaction.StartSequence`

A visible tombstone means the key is absent at that boundary. If every version in the chain is newer than the boundary, the key is absent from that snapshot.

`VersionVisibility` is the authoritative committed-version predicate. Other subsystems must not invent a separate rule.

## Publication

v0.3 intentionally keeps commit publication under the database-wide gate. WAL durability is established first, physical current-state storage is reconciled second, and immutable version heads are then published while all facade readers are excluded by the same gate. This is a correctness baseline, not the final concurrency design.

## Recovery

Commit WAL records persist the logical commit sequence. On open, recovery validates committed transactions, replays final current state into the physical store, then rebuilds the complete in-memory version history from committed WAL transactions in commit order.

Legacy v0.2 Commit records with an empty payload remain readable; recovery assigns them monotonically increasing sequences in WAL commit order.

## Pre-WAL development databases

If the append-only storage contains current keys that have no WAL-backed version chain, v0.3 bootstraps those keys at the database's current open boundary. This preserves current-state compatibility for early v0.1 development databases without pretending that pre-WAL historical boundaries are reconstructable. Once a key is modified through the WAL-backed facade, normal version history applies.
