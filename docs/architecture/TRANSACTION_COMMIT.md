# v0.3 transaction commit boundary

The v0.3 facade serializes commit publication through one managed database gate while allowing multiple transactions to remain active with independent snapshot boundaries.

Before any WAL byte is appended, commit performs the following work:

1. read the transaction's final local write set;
2. validate first-committer-wins conflicts against the newest committed version for every written key;
3. allocate the next logical commit sequence;
4. encode and validate every WAL mutation;
5. validate every storage mutation and overflow calculation;
6. capture the current physical data-file length as the recovery base.

A conflict aborts the transaction without touching the WAL and does not fault the database.

The durable path then appends `Begin`, one `Put`/`Delete` record per final mutation, and one `Commit` record. The Commit payload stores the logical commit sequence and the data-file length observed before physical publication. ChronicleDB disables per-record WAL flushing on this path and performs one explicit stable-storage flush after the Commit record.

A successful WAL flush transitions the transaction to `DurableCommitted`; abort is impossible after this point. Physical current-state pages are then reconciled. Immutable MVCC versions and index heads are published while the database gate excludes readers, the transaction becomes `Committed`, and the database current sequence advances.

Any exception after WAL I/O begins faults the database instance. The caller must close and reopen so WAL recovery can resolve the durable outcome.
