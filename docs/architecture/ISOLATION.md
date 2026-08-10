# v0.3 isolation contract

ChronicleDB v0.3 provides **Snapshot Isolation**, not Serializable Isolation.

## Guarantees

For an active transaction:

- reads use one stable `StartSequence` for the transaction lifetime;
- commits newer than that boundary are not visible;
- the transaction always sees its own latest local write;
- uncommitted writes from other transactions are never visible;
- a later tombstone does not hide a value from an older snapshot;
- two writers that overlap on a logical key cannot both commit when one has committed a version newer than the other's start sequence.

The current conflict policy is first-committer-wins. Conflict validation happens before the transaction writes any WAL record.

## Non-guarantees

Snapshot Isolation is not serializability. Transactions that read overlapping state but write different keys can both commit. Therefore anomalies such as write skew remain possible.

For example, two transactions can both read `A = 1, B = 1`; one can write `A = 0` while the other writes `B = 0`; because their write sets do not overlap, both may commit and produce `A = 0, B = 0`.

This behavior is intentional in v0.3 and is covered by tests so that later changes do not accidentally claim stronger isolation.
