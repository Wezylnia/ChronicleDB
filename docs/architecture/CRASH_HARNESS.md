# v0.2 crash harness

`ChronicleDB.CrashHarness` runs the same deterministic two-key transaction in a child process for every transaction fault point. The child uses `Environment.FailFast` so file handles are not closed through normal managed disposal. The parent reopens the database and validates the recovery result.

Before the WAL flush boundary, the harness accepts the transaction as either absent or complete because an operating system may persist buffered writes despite the missing application flush. After `AfterWalFlush`, the transaction must be complete. The harness is intentionally a tool-level executable; storage, WAL, transaction, and recovery logic remain in their owning production projects.
