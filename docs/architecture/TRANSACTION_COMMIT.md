# v0.2 transaction commit boundary

The v0.2 facade serializes database operations through one managed gate. A transaction first keeps its mutations private, then appends `Begin`, one `Put`/`Delete` record per mutation, and `Commit` to the WAL. The WAL is flushed before the storage batch is published.

Before `Begin` is appended, the complete write set is validated against both the storage record format and WAL mutation/record limits. This prevents a mutation that cannot be replayed from becoming a durable decision.

After the `Commit` record is appended, the facade performs one explicit WAL flush. A successful flush transitions the transaction to `DurableCommitted`; from that point abort is forbidden. The storage batch then validates again, appends physical records, flushes, and only then replaces the in-memory current-state entries. Reads through the facade use the same gate, so they observe either the state before the batch or the complete batch. Any exception after WAL I/O faults the database instance and requires reopen/recovery.
