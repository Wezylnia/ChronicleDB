# v0.2 transaction state and local writes

Each transaction has a non-empty `TransactionId`, a stable start sequence, an explicit state, and a private write set. `Begin` transitions `Created -> Active`; mutations are accepted only while active.

Commit preparation follows `Active -> Preparing -> Committing -> DurableCommitted -> Committed`. `DurableCommitted` is the point after the WAL commit record has crossed the explicit flush barrier; it cannot transition to abort. Abort follows `Created|Active|Preparing|Committing -> Aborting -> Aborted`. Any other transition fails deterministically. A transaction that has reached a terminal state cannot be written again.

Writes are keyed by full binary key equality and replace an earlier mutation to the same key inside the transaction. Local reads return cloned values, deletes behave as local absence, and the write set is never published to the committed store by this descriptor alone. Terminal transitions release private value buffers.
