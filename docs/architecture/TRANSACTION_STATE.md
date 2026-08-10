# v0.5 transaction state and local writes

Each transaction has a non-empty `TransactionId`, an immutable `StartSequence`, an optional final `CommitSequence`, an explicit state, and a private write set. A public transaction handle is intended to be single-owner; concurrency is achieved through independent handles.

## State machine

Normal commit:

`Created -> Active -> Preparing -> Committing -> DurableCommitted -> Committed`

Normal abort:

`Created|Active|Preparing -> Aborting -> Aborted`

Uncertain WAL outcome:

`Preparing|Committing -> Indeterminate`

`DurableCommitted` means the Commit record has crossed ChronicleDB's explicit WAL durability barrier. Abort is impossible from `Committing` once WAL publication has begun through the facade, and it is categorically impossible from `DurableCommitted`. `Indeterminate` is terminal for the in-process handle: only reopen/recovery is authoritative about whether the WAL transaction survived.

## Write-set freeze

Mutations are accepted only while `Active`. Repeated mutations of one full binary key replace the earlier local mutation. `PrepareAndGetWriteSet()` changes `Active -> Preparing` and snapshots the complete final write set while holding the transaction's own synchronization gate. A concurrent caller therefore cannot successfully add a write after the commit thread has frozen the transaction.

## Reads

The transaction always checks its local write set first. A local tombstone means absence. If a key has no local mutation, the database resolves committed history at the transaction's fixed `StartSequence`. Later commits never move that boundary.
