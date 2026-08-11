# v1.0 storage format

This document describes the byte-level persistent storage owned by `ChronicleDB.Storage`. WAL framing is documented separately.

## Files

| File | Shape | Purpose |
| --- | --- | --- |
| `chronicle.meta` | append-only 64-byte header generations | database identity, format capabilities |
| `chronicle.data` | `N * 16,384` bytes except an untrusted crash tail during recovery | append-only record/overflow pages |
| `chronicle.snapshots` | 64-byte header + framed lifecycle records | persistent named snapshot roots |
| `chronicle.history-roots` | 64-byte header + fixed 120-byte lifecycle records | generalized retained history-root registry |
| `chronicle.branches` | 64-byte header + variable framed lifecycle/commit-prefix records | branch identity, ancestry, activation, local committed prefix |
| `branches/<BranchId>/chronicle.data` | page-aligned append-only branch-private version records | branch-local MVCC history |
| `branches/<BranchId>/chronicle.snapshots` | framed lifecycle records | persistent snapshots inside one branch |
| `branches/<BranchId>/branch.wal` | common WAL framing + branch/history payload envelope | branch-local durability and recovery |
| `chronicle.history` / `branches/<BranchId>/chronicle.history` | immutable checksummed retained-history projection | v0.9 checkpoint used before WAL rotation/physical reclamation |

`chronicle.wal` is owned by `ChronicleDB.Wal`.

## Limits

- page size: exactly 16 KiB;
- binary keys may be zero length; the default maximum key is 1,024 bytes;
- default maximum value: 64 MiB;
- configured storage value limit can never exceed 256 MiB, while ChronicleDB's WAL-backed facade is limited by the 64 MiB mutation protocol;
- record pages contain one logical record payload in the current append-only layout;
- page IDs are one-based `UInt64`; zero is invalid;
- the current `FileStream`/`long` offset model limits `chronicle.data` to at most 562,949,953,421,311 full 16 KiB pages (largest aligned length below `Int64.MaxValue`);
- persistent snapshot names are at most 1,024 valid UTF-8 bytes.

All encoded lengths are validated before allocation/slicing/file access. Persistent WAL/checkpoint codecs intentionally use wider absolute framing limits than an individual database may configure; Main and branch recovery therefore reapply the effective database `MaxKeySize` and `MaxValueSize` before recovered logical history is replayed or used to rebuild physical state.

## Database metadata journal

Each `chronicle.meta` slot is 64 bytes. v1.4 slots use:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | `CHDBv001` |
| 8 | 2 | major `1` |
| 10 | 2 | minor `4` |
| 12 | 4 | slot size `64` |
| 16 | 16 | database GUID |
| 32 | 4 | page size |
| 36 | 4 | CRC32C algorithm ID |
| 40 | 4 | monotonic capability flags |
| 44 | 8 | creation Unix milliseconds |
| 52 | 8 | strictly increasing metadata generation |
| 60 | 4 | CRC32C of bytes `0..59` |

Capability flags record that WAL, persistent snapshot metadata, the generalized history-root registry, the branch metadata journal, and retained-history checkpoints have been durably initialized. Once a flag is present, a later generation may not remove it. This prevents accidental deletion of a critical persistence file from being mistaken for a first-time upgrade.

Legacy v1.0 single-slot headers remain readable only with zero flags/reserved bytes. Their in-memory generation is zero; the first capability update appends a v1.1 generation.

A partial **final** metadata slot can be discarded because an earlier complete checksummed generation remains authoritative. A corrupt complete slot is fatal.

Initial metadata creation uses a fully flushed temporary file followed by an atomic same-directory move; an existing empty canonical metadata file is corruption, not an invitation to invent a new database identity.

## Data pages

Every 16 KiB page uses the existing 32-byte `CPG1` header with page type, one-based ID, generation, payload length, reserved fields, and CRC32C over the whole zero-padded page. Page types are `Record` and `Overflow`.

Record payloads retain the v0.1 layout: key length, value length, flags, overflow head, inline length, full key bytes, and inline bytes. Overflow pages form forward-only chains and must reconstruct exactly the declared value length.

The append-oriented page model may retain old physical records between maintenance passes; current physical state is rebuilt by scanning newest record/tombstone state per key. v0.9+ GC/compaction may replace that representation only after retained logical history is durably checkpointed. Once the database metadata says WAL is initialized, the high-level engine requires every physical current key to have WAL-backed logical history; newly injected low-level keys are rejected as persistence divergence rather than silently adopted.

## Persistent snapshot file

`chronicle.snapshots` begins with a checksummed 64-byte `CHSNAP01` header containing:

- format version 1.0;
- database GUID;
- durable historical `RetentionFloor`;
- checksum algorithm;
- maximum UTF-8 name bytes.

It is followed by Create/Delete lifecycle records. Records have a 64-byte fixed header, UTF-8 name payload for Create, and an 8-byte footer. Framing stores total length redundantly in the header and footer; CRC32C covers the complete record with the checksum field zeroed. Event sequences are contiguous and snapshot IDs are never reusable.

Delete records remove the named persistent root only. v0.5 does not reclaim committed history.

## Persistent history-root file

`chronicle.history-roots` begins with a checksummed 64-byte `CHROOT01` header containing the database GUID and the main `HistoryId`. It is followed by fixed-size 120-byte `HRT1` records. A Create record publishes an active root; a Delete record publishes the same root metadata with the deleted state. Records contain the root ID, root kind, lifecycle state, owner database, history domain, optional parent history, visibility boundary, creation time, redundant frame lengths, footer magic, and CRC32C.

The storage layer keeps lifecycle records database-bound and append-only. The semantic registry exposes Creating and Deleting intents while the durable protocol publishes only complete Active or Deleted outcomes. A crash before a complete frame leaves no new active root; a complete flushed frame is recovered even if acknowledgement was lost. Root IDs are never reused.

## Corruption versus crash tail

Complete checksummed structures are never silently discarded. Automatic repair is restricted to:

- partial final database-metadata generation;
- incomplete final WAL/snapshot frame after validated framing;
- incomplete final history-root frame after validated framing;
- incomplete final branch-metadata frame after validated redundant length framing;
- Main data append regions whose recovery base is proven by a durable WAL Commit, plus the narrow legacy partial-final-page rule;
- branch-local append bytes beyond the latest durably published v0.7 branch committed-prefix descriptor.
### Faulted low-level store instances

A `PersistentKeyValueStore` instance is not reusable after an operation may have modified
persistent bytes and then failed. The store enters a faulted/recovery-required state and
rejects further reads and writes until it is closed and reopened. A fault injected before
the first page write does not fault the instance because persistence was not touched.
This mirrors the WAL and snapshot-metadata lifetime rule and prevents callers of the
low-level storage API from continuing with an in-memory index whose physical append
outcome is uncertain.


## v0.7 branch metadata and local versions

`chronicle.branches` begins with a checksummed `CHBRN001` header bound to the Main database and Main history identities. Variable records contain redundant header/footer lengths, CRC32C, contiguous event sequence, `BranchId`, child/parent `HistoryId`, base root and parent boundary, local storage identity, local commit sequence, transaction identity, mutation count, committed data length, branch depth, creation time, and UTF-8 name. Complete corrupt frames are fatal; only an incomplete final frame may be truncated.

Each activated branch owns a separate branch-local page store under `branches/<BranchId>/`. Physical keys identify individual local versions rather than logical user keys. The stored `BVR1` envelope carries full user-key bytes, branch/history/transaction identity, local commit sequence, mutation index/count, tombstone state, value bytes, and CRC32C. The parent dataset is not copied into this store. `AdvanceSequence` metadata records define the authoritative page-aligned committed prefix used by v0.7 reopen.

## v0.8 branch WAL envelope

Each branch uses the standard 64-byte WAL file header and standard record framing. The inner record payload is wrapped by a fixed branch envelope that redundantly identifies `BranchId` and `HistoryId`. Recovery verifies these identities before interpreting Begin/Put/Delete/Commit payloads. The branch WAL file name is `branch.wal` and its generic file identity is the branch-local storage GUID.

## v0.9 retained-history checkpoint

`chronicle.history` is a complete immutable file for one history domain. Its checksummed header contains database/storage identity, `HistoryId`, checkpoint sequence, generic retention floor, and version count. Each version record contains transaction identity, commit sequence, full binary key (including the valid zero-length key), tombstone/value metadata, explicit lengths, and CRC32C. Declared record counts and payload lengths must be physically possible for the containing file before variable-size allocations are attempted, and disk-declared counts do not directly determine unbounded collection capacity. The entire file must parse exactly; unexplained trailing bytes are corruption. After framing/CRC validation, the high-level recovery path also checks every retained key/value against the database's configured logical limits before admitting the checkpoint into MVCC history.

Publication writes and fsyncs a temporary file, moves the previous checkpoint to `.previous`, publishes the replacement, re-reads it for validation, then retires the previous generation. A checkpoint becomes required only after the `HistoryCheckpointInitialized` database-header capability is durable.

## v0.9 lifecycle journal compaction

Snapshot, history-root, and branch lifecycle journals remain append-only during foreground operation. A GC maintenance pass may atomically rewrite them to canonical active state after all durable history/reclamation decisions for the pass are complete. Incomplete lifecycle states are never silently converted into active roots during compaction.
