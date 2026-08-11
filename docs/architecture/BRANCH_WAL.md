# Branch WAL

Every writable branch in ChronicleDB v1.0 has an independent logical WAL stream. The file is stored as `branches/<BranchId>/branch.wal`; it uses the common ChronicleDB WAL framing, LSN validation, CRC32C checksums, and durability barrier, but it is not interchangeable with Main WAL.

## Identity binding

The WAL file header is bound to the branch-local storage `DatabaseId`. In addition, every Begin, Put, Delete, and Commit payload is wrapped by a branch envelope containing the persistent `BranchId` and `HistoryId`. Recovery validates both values on every record before decoding the inner payload. Copying a WAL from another branch, another history domain, or Main therefore fails closed even when the generic WAL framing is otherwise valid.

## Commit authority

Branch WAL is the transaction durability authority for initialized v1 histories. A branch commit performs:

1. freeze the transaction write set;
2. validate Snapshot Isolation write/write conflicts inside the branch history;
3. preflight storage, MVCC, metadata, and WAL capacity;
4. allocate the next branch-local `CommitSequence`;
5. append Begin and mutation records;
6. append the Commit record containing the local commit sequence and the pre-publication physical data length;
7. fsync the branch WAL;
8. enter the one-way durable-commit state;
9. publish branch-local physical version records;
10. publish the branch metadata cache/physical boundary;
11. publish committed versions to the in-memory MVCC index;
12. acknowledge success.

A deterministic validation failure is not allowed after the durable decision. An environmental failure after WAL fsync is recovery-defined: the database becomes recovery-required, and reopen redoes the durable branch transaction.

## Legacy v0.7 migration

A v0.7 branch may have metadata commit descriptors and branch-local version pages but no `branch.wal`. When v1.0 first opens that legacy branch, ChronicleDB validates that complete legacy history, writes an equivalent branch WAL, fsyncs it, and only then publishes the branch-local `WalInitialized` capability flag. A crash before the flag is published leaves the partial WAL non-authoritative; the next open deletes it and repeats bootstrap from the still-authoritative v0.7 state.

Once `WalInitialized` is durable, a missing branch WAL is corruption rather than a legacy-upgrade signal.

## Checkpoint interaction

After a complete retained-history checkpoint is published, the branch WAL may be reset to its header. Recovery then starts from the checkpoint sequence and replays only later WAL commits. A crash before reset leaves checkpoint plus the older WAL; records at or below the checkpoint are validated but not replayed. A crash after reset uses checkpoint plus the new WAL prefix.
