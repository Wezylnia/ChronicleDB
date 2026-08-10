# v0.1 Storage Format

This is the byte-level contract implemented by `ChronicleDB.Storage`.

## Files

| File | Size | Purpose |
| --- | ---: | --- |
| `chronicle.meta` | exactly 64 bytes | database identity and format configuration |
| `chronicle.data` | `N * 16,384` bytes | append-only fixed-size pages |

The v0.1 store does not create a WAL. Transactional durability starts in v0.2.

## Limits

- page size: exactly 16,384 bytes;
- maximum key size: 1,024 bytes by default, never more than `UInt16.MaxValue`;
- maximum value size: 64 MiB by default, never more than 256 MiB;
- page ID: unsigned 64-bit, one-based; zero is invalid;
- all length fields are checked before allocation, slicing, or file access.

The configured limits are validated when opening the database. A header with a different page size is incompatible with the requested options.

## Database header (`chronicle.meta`)

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII magic `CHDBv001` |
| 8 | 2 | major format version (`1`) |
| 10 | 2 | minor format version (`0`) |
| 12 | 4 | header size (`64`) |
| 16 | 16 | database GUID bytes |
| 32 | 4 | page size (`16,384`) |
| 36 | 4 | checksum algorithm (`1` = CRC32C) |
| 40 | 4 | format flags (currently zero) |
| 44 | 8 | creation timestamp, Unix milliseconds |
| 52 | 8 | reserved bytes (must be zero) |
| 60 | 4 | CRC32C of offsets `0..59` |

Unknown major versions, unsupported checksum algorithms, non-zero reserved bytes, invalid GUIDs, wrong header size, and checksum failures are rejected.

## Page header

Every 16 KiB page begins with a 32-byte header.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | ASCII magic `CPG1` |
| 4 | 1 | page type (`1` record, `2` overflow) |
| 5 | 1 | page flags (currently zero) |
| 6 | 2 | page header size (`32`) |
| 8 | 8 | one-based page ID |
| 16 | 8 | generation marker (`1` in v0.1) |
| 24 | 2 | payload length |
| 26 | 2 | reserved bytes (must be zero) |
| 28 | 4 | CRC32C of the complete page with this field treated as zero |

Bytes after `header + payload length` and before the page end are zero-filled and included in the checksum. A page with an unexpected ID, type, header size, reserved field, payload length, or checksum is corrupt.

## Record payload

The record page payload begins with a 24-byte record header.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 1 | record version (`1`) |
| 1 | 1 | flags: bit 0 tombstone, bit 1 overflow value |
| 2 | 2 | key length |
| 4 | 4 | value length |
| 8 | 8 | overflow head page ID, or zero |
| 16 | 4 | inline value length |
| 20 | 4 | reserved bytes (must be zero) |
| 24 | `key length` | full binary key |
| 24 + key length | `inline length` | inline value bytes |

Record flags and lengths must agree. Tombstones have zero value length and no overflow head. Inline records have `inline length == value length` and a zero overflow head. Overflow records have `inline length == 0` and a non-zero overflow head.

## Overflow payload

An overflow page payload begins with a 16-byte header:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | next overflow page ID, or zero for the tail |
| 8 | 4 | chunk length |
| 12 | 4 | reserved bytes (must be zero) |
| 16 | `chunk length` | value bytes |

The chain must be forward-only, must not repeat a page, and must produce exactly the record’s declared value length. A chain with a missing page, cycle, short result, or excess result is corruption.

## Error categories

- `StorageFormatException`: incompatible version, unsupported algorithm, or invalid format configuration;
- `StorageCorruptionException`: malformed/truncated/checksum-invalid bytes;
- `StorageLimitException`: key/value/configuration exceeds documented limits;
- `StorageException`: general storage lifecycle or I/O boundary failure.

## Compatibility

The v0.1 format is development-stable, not a promise of cross-release migration. Any incompatible byte-level change must increment the major format version and add an ADR, golden fixtures, corruption tests, and an explicit migration/rejection policy.
