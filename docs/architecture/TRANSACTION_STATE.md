# v0.3 transaction state and local writes

Each transaction has a non-empty `TransactionId`, an immutable `StartSequence`, an optional final commit sequence, an explicit state, and a private write set.

`Begin` transitions `Created -> Active`. Mutations are accepted only while active. Repeated mutations to the same full binary key replace the earlier local mutation, so one transaction contributes at most one committed logical version per key.

Commit preparation follows:

`Active -> Preparing -> Committing -> DurableCommitted -> Committed`

`DurableCommitted` means the WAL Commit record containing the final commit sequence has crossed the explicit durability barrier. From that state abort is forbidden; failure requires recovery.

Abort follows:

`Created|Active|Preparing|Committing -> Aborting -> Aborted`

Commit preparation atomically freezes the write set and moves the transaction to `Preparing`. First-committer-wins validation then runs in that state; a conflict aborts the transaction and writes no WAL records.

Local reads return the transaction's newest local value first. A local delete behaves as absence. If the key has no local mutation, the facade reads committed MVCC history at the transaction's fixed `StartSequence`.
