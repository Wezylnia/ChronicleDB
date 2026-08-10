# v0.2 transaction commit boundary

The v0.2 facade serializes database operations through one managed gate. A transaction first keeps its mutations private, then appends `Begin`, one `Put`/`Delete` record per mutation, and `Commit` to the WAL. The WAL is flushed before the storage batch is published.

The storage batch validates all mutations, appends physical records, flushes, and only then replaces the in-memory current-state entries. Reads through the facade use the same gate, so they observe either the state before the batch or the complete batch. The WAL commit is the durable decision; startup replay is implemented by the Recovery layer.
