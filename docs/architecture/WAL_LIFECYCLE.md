# WAL lifecycle and tail policy

`WalLog` owns one `.wal` file exclusively. Its fixed file header contains the storage database GUID and prevents replaying a WAL from another database. Appends allocate monotonically increasing LSNs while holding the log lock. The standalone log default flushes each complete record to the stable-storage barrier before returning from `Append`; the ChronicleDB transaction path deliberately disables per-append flushing and performs one explicit flush after `Commit`.

On open, records are scanned sequentially. A final incomplete header or payload is treated as an interrupted tail and truncated to the last complete record. A complete record with an invalid checksum, unsupported format, invalid length, or non-monotonic LSN is corruption and fails open; it is never silently discarded.

`ReadAll` exposes only complete records. The log is not a transaction manager: grouping records by transaction and replaying committed effects belongs to the Recovery/Transactions layers.
