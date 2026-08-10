# v0.3 crash harness

`ChronicleDB.CrashHarness` runs a deterministic two-key transaction in a child process for every transaction fault point. The child uses `Environment.FailFast` so file handles are not closed through normal managed disposal. The parent reopens the database and validates the recovered state.

The harness also includes an `AfterFirstPhysicalPage` scenario. In that case the WAL Commit decision has already crossed the durability barrier, the child is terminated after the first data-page write, and recovery must reconstruct the complete two-key transaction from WAL. This directly exercises partial physical publication rather than testing only the boundaries around it.

At `BeforeWalAppend`, the transaction must be absent. After WAL bytes have been appended but before the explicit flush boundary, the harness accepts the transaction as either absent or complete because an operating system may persist buffered writes despite the missing application flush. A partial transaction is never accepted. At and after the durable boundary, including the physical-page crash scenario, the transaction must recover completely.

The harness is intentionally a tool-level executable; storage, WAL, transaction, MVCC, and recovery logic remain in their owning production projects.
