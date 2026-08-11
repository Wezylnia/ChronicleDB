# Transaction Model

ChronicleDB transactions are explicit single-history Snapshot Isolation transactions over binary keys and values.

A transaction owns:

- immutable `TransactionId`;
- immutable `StartSequence` captured at begin;
- private final write set keyed by full binary key;
- explicit state machine;
- optional final `CommitSequence` after durable decision.

`Put` and `Delete` copy caller-owned input into engine-owned transaction state. Repeated mutations of a key replace the prior local mutation. `TryGet` implements read-your-writes before consulting committed history at `StartSequence`.

Commit uses first-committer-wins validation. A newer committed version of any written key causes `TransactionConflictException` and an abort before WAL. Disjoint write sets may commit even when they read overlapping values; this is Snapshot Isolation, not serializability.

The public transaction handle should be treated as single-owner. Internal locking makes state transitions deterministic and prevents write-set mutation from racing commit freeze, but it is not an invitation to issue arbitrary operations concurrently on one handle.

See [TRANSACTION_STATE](TRANSACTION_STATE.md), [TRANSACTION_COMMIT](TRANSACTION_COMMIT.md), and [ISOLATION](ISOLATION.md) for the detailed contracts.
