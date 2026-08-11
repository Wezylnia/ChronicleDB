# Transaction Commit Protocol

ChronicleDB v1.0 preserves the v0.5 durability rule while scoping serialization to the writable history. Main has one durability-critical commit coordinator, and every branch has its own coordinator. This is not a database-wide read lock: ordinary current reads, historical reads, persistent snapshot reads, transaction construction, and commits in independent branch histories do not share one global fsync gate. Within one history, the serialized region preserves an unambiguous order across commit-sequence allocation, WAL LSNs, physical recovery bases, and final publication.

## Preflight phase

Before any WAL byte is appended, commit:

1. atomically freezes and copies the transaction's final local write set;
2. validates first-committer-wins conflicts against newest committed versions;
3. allocates the next logical commit sequence;
4. encodes every WAL mutation and validates WAL record capacity;
5. validates storage key/value limits, overflow calculations, and final file-length arithmetic;
6. validates MVCC version-handle and chain-length capacity;
7. captures the current `chronicle.data` length as the recovery base;
8. encodes the Commit payload.

The invariant is strict: **no known deterministic format/limit validation is intentionally deferred until after durable commit**.

## Durable path

The WAL path appends `Begin`, the final `Put`/`Delete` mutation for every written key, then `Commit`. Transactional use disables per-record fsync and performs one explicit `Flush(flushToDisk: true)` after the complete Commit record.

The Commit payload contains:

- logical `CommitSequence`;
- `chronicle.data` length before this transaction's physical publication.

After that flush succeeds, the transaction enters `DurableCommitted`. ChronicleDB then reconciles append-only physical current-state pages, publishes all immutable MVCC versions under one version-store writer section, marks the transaction `Committed`, and finally publishes the new current sequence. A reader therefore uses either the previous sequence boundary or the complete new version set.

## Failure policy

- failure before WAL append: abort the transaction; database may remain open;
- failure after WAL I/O may have started but before durable decision: mark transaction `Indeterminate`, fault the database/WAL, and require reopen;
- failure after durable commit: never abort; fault the database and require recovery;
- conflict: abort before WAL and return `TransactionConflictException`;
- faulted database: reject ordinary operations until reopened.

This conservative policy prevents cleanup code from pretending it knows an I/O outcome it cannot prove.
