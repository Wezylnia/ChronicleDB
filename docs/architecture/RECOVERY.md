# v0.2 WAL recovery

Recovery scans complete WAL records, validates transaction structure, ignores transactions that end without `Commit`, and discards explicit `Abort` transactions. A transaction ID may appear only once.

Committed mutations are folded in WAL order to one final desired state per full binary key. The store then reconciles that final state under its publication lock. This makes recovery idempotent and prevents an older committed transaction from overwriting a newer value when both were already partially applied before a crash.

The v0.2 policy is conservative: malformed transaction structure, invalid mutation payloads, checksum failures, and non-monotonic WAL records fail startup. Incomplete final WAL bytes are handled by `WalLog` before transaction recovery and are truncated to the last complete record.
