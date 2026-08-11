# v1.0 WAL format

Each WAL begins with a fixed 64-byte database-bound file header (`CWLHDR01`, version 1.0). The header contains database GUID, first LSN, checksum algorithm, reserved bytes, and CRC32C. A WAL whose GUID differs from `chronicle.meta` is rejected.

## Record framing

Records begin at byte 64 and have a 48-byte header. ChronicleDB reads record versions 1 and 2; new records are written as **version 2**.

Common fields:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | `CWL1` |
| 4 | 1 | record version |
| 5 | 1 | type: Begin/Put/Delete/Commit/Abort |
| 6 | 2 | flags, zero |
| 8 | 2 | header size `48` |
| 10 | 2 | reserved, zero |
| 12 | 8 | contiguous LSN |
| 20 | 16 | transaction GUID |
| 36 | 4 | payload length |
| 40 | 4 | version-dependent framing field |
| 44 | 4 | record CRC32C |

Version 1 uses offset 40 as checksum-algorithm ID `1`. Version 2 fixes CRC32C by record version and stores `~payloadLength` at offset 40. The redundant value lets the scanner reject an internally inconsistent complete header rather than misclassifying some length-field corruption as a crash-truncated payload.

LSNs must be exactly contiguous beginning at one. Merely increasing LSNs are insufficient because a missing complete record would otherwise be invisible.

The record envelope supports 65 MiB so a maximum 64 MiB mutation value still has room for encoded key/length metadata. These are **format envelope limits**, not permission to exceed the database's configured logical `MaxKeySize` / `MaxValueSize`. Recovery revalidates every committed mutation against the opened database limits before physical redo or MVCC publication; a checksummed record outside those limits is semantic corruption for that database configuration.

## Commit payload

Current Commit payload is 16 bytes:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | non-zero logical commit sequence |
| 8 | 8 | `chronicle.data` length before physical publication |

Recovery also accepts empty legacy v0.2 Commit payloads and 8-byte sequence-only development payloads.

## Durability

The ChronicleDB facade opens the WAL with `FlushOnAppend = false`, appends the complete transaction, then executes one explicit stable-storage flush after Commit. A successful durable commit is never acknowledged before this barrier.

A WAL instance faults after uncertain append/flush I/O. It cannot be reused; database recovery must reopen and scan the durable prefix/tail. Cleanup deliberately does not issue an extra explicit durability flush on a faulted WAL.

## Branch WAL payload envelope

v0.8 branch WAL files reuse this generic framing but wrap every record payload with a branch envelope containing `BranchId` and `HistoryId`. The generic WAL header is bound to the branch-local storage identity; the envelope additionally prevents a syntactically valid WAL record from being replayed into another branch history. The inner Begin payload is empty, Put/Delete use the normal mutation codecs, and Commit uses the normal commit-sequence/recovery-base codec.
