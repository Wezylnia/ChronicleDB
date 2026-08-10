# v0.5 storage format

This document describes the byte-level persistent storage owned by `ChronicleDB.Storage`. WAL framing is documented separately.

## Files

| File | Shape | Purpose |
| --- | --- | --- |
| `chronicle.meta` | append-only 64-byte header generations | database identity, format capabilities |
| `chronicle.data` | `N * 16,384` bytes except an untrusted crash tail during recovery | append-only record/overflow pages |
| `chronicle.snapshots` | 64-byte header + framed lifecycle records | persistent named snapshot roots |

`chronicle.wal` is owned by `ChronicleDB.Wal`.

## Limits

- page size: exactly 16 KiB;
- default maximum key: 1,024 bytes;
- default maximum value: 64 MiB;
- configured storage value limit can never exceed 256 MiB, while ChronicleDB's WAL-backed facade is limited by the 64 MiB mutation protocol;
- record pages contain one logical record payload in the current append-only layout;
- page IDs are one-based `UInt64`; zero is invalid;
- the current `FileStream`/`long` offset model limits `chronicle.data` to at most 562,949,953,421,311 full 16 KiB pages (largest aligned length below `Int64.MaxValue`);
- persistent snapshot names are at most 1,024 valid UTF-8 bytes.

All encoded lengths are validated before allocation/slicing/file access.

## Database metadata journal

Each `chronicle.meta` slot is 64 bytes. v1.1 slots use:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | `CHDBv001` |
| 8 | 2 | major `1` |
| 10 | 2 | minor `1` |
| 12 | 4 | slot size `64` |
| 16 | 16 | database GUID |
| 32 | 4 | page size |
| 36 | 4 | CRC32C algorithm ID |
| 40 | 4 | monotonic capability flags |
| 44 | 8 | creation Unix milliseconds |
| 52 | 8 | strictly increasing metadata generation |
| 60 | 4 | CRC32C of bytes `0..59` |

Capability flags currently record that WAL and persistent snapshot metadata have been durably initialized. Once a flag is present, a later generation may not remove it. This prevents accidental deletion of a critical persistence file from being mistaken for a first-time upgrade.

Legacy v1.0 single-slot headers remain readable only with zero flags/reserved bytes. Their in-memory generation is zero; the first capability update appends a v1.1 generation.

A partial **final** metadata slot can be discarded because an earlier complete checksummed generation remains authoritative. A corrupt complete slot is fatal.

Initial metadata creation uses a fully flushed temporary file followed by an atomic same-directory move; an existing empty canonical metadata file is corruption, not an invitation to invent a new database identity.

## Data pages

Every 16 KiB page uses the existing 32-byte `CPG1` header with page type, one-based ID, generation, payload length, reserved fields, and CRC32C over the whole zero-padded page. Page types are `Record` and `Overflow`.

Record payloads retain the v0.1 layout: key length, value length, flags, overflow head, inline length, full key bytes, and inline bytes. Overflow pages form forward-only chains and must reconstruct exactly the declared value length.

The append-only page model deliberately retains old physical records in v0.5; current state is rebuilt by scanning newest record/tombstone state per key. Once the database metadata says WAL is initialized, the high-level engine requires every physical current key to have WAL-backed logical history; newly injected low-level keys are rejected as persistence divergence rather than silently adopted.

## Persistent snapshot file

`chronicle.snapshots` begins with a checksummed 64-byte `CHSNAP01` header containing:

- format version 1.0;
- database GUID;
- durable historical `RetentionFloor`;
- checksum algorithm;
- maximum UTF-8 name bytes.

It is followed by Create/Delete lifecycle records. Records have a 64-byte fixed header, UTF-8 name payload for Create, and an 8-byte footer. Framing stores total length redundantly in the header and footer; CRC32C covers the complete record with the checksum field zeroed. Event sequences are contiguous and snapshot IDs are never reusable.

Delete records remove the named persistent root only. v0.5 does not reclaim committed history.

## Corruption versus crash tail

Complete checksummed structures are never silently discarded. Automatic repair is restricted to:

- partial final database-metadata generation;
- incomplete final WAL/snapshot frame after validated framing;
- data append regions whose recovery base is proven by a durable WAL Commit, plus the narrow legacy partial-final-page rule.
### Faulted low-level store instances

A `PersistentKeyValueStore` instance is not reusable after an operation may have modified
persistent bytes and then failed. The store enters a faulted/recovery-required state and
rejects further reads and writes until it is closed and reopened. A fault injected before
the first page write does not fault the instance because persistence was not touched.
This mirrors the WAL and snapshot-metadata lifetime rule and prevents callers of the
low-level storage API from continuing with an in-memory index whose physical append
outcome is uncertain.
