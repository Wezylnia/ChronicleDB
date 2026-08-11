# WAL lifecycle and tail policy

`WalLog` owns one `.wal` file exclusively. Its fixed file header contains the storage database GUID and prevents replaying a WAL from another database. Initial creation writes and flushes a unique temporary file before moving it to the canonical name; an existing truncated canonical header is corruption rather than a reason to invent a new log identity.

Appends allocate **contiguous** LSNs while holding the log lock. The standalone low-level default may flush on each append; ChronicleDB's transaction path explicitly opens the log with per-append flushing disabled and performs one stable-storage flush after the complete Commit record.

## Tail policy

On open, records are scanned sequentially. Version-2 record headers contain redundant payload-length information, so a complete header must validate internally before an incomplete payload can be classified as a crash tail.

- short final header: truncate to prior valid record;
- internally valid header + incomplete final payload: truncate to prior valid record;
- complete record checksum failure: corruption;
- invalid framing/version/type/identity: corruption;
- LSN gap/duplicate/reordering: corruption.

`ReadAll` exposes only complete records.

## Faulted log

If append or explicit flush encounters uncertain I/O, the `WalLog` instance enters a faulted state and cannot be reused. ChronicleDB also faults the owning database and marks a pre-durability transaction indeterminate. Recovery after reopen is the authority on the durable prefix.

A faulted `WalLog.Dispose()` deliberately avoids issuing another explicit `Flush(true)` that could be mistaken for the transaction's missing application durability barrier. File-handle cleanup itself is not treated as a successful commit acknowledgement.

The log is not a transaction manager: transaction grouping, commit semantics, recovery bases, MVCC reconstruction, and historical retention belong to higher layers.

## v0.9 checkpoint rotation

A history WAL may be reset only after a complete equivalent retained-history checkpoint has been fsynced and, on first use, the checkpoint capability has been durably published. Reset truncates the WAL back to its header and restarts local LSN allocation. Recovery accepts either checkpoint plus a pre-reset WAL or checkpoint plus the post-reset WAL prefix; commit records at or below the checkpoint sequence are validated but not replayed.
