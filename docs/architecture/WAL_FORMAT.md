# v0.3 WAL file and record format

Each WAL starts with a fixed 64-byte file header. The header binds the log to the storage database identity; a database never replays a WAL belonging to another database. The WAL file header remains format version `1.0`. The v0.3 Commit payload is a backward-compatible, length-delimited record-level extension, so existing v0.2 WAL headers do not require an in-place rewrite before new commits are appended.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII magic `CWLHDR01` |
| 8 | 2 | major version (`1`) |
| 10 | 2 | minor version (`0`) |
| 12 | 4 | header size (`64`) |
| 16 | 16 | database GUID bytes |
| 32 | 8 | first LSN (`1`) |
| 40 | 4 | checksum algorithm (`1` = CRC32C) |
| 44 | 16 | reserved (zero) |
| 60 | 4 | CRC32C of bytes `0..59` |

Records begin at offset `64` and use little-endian encoding.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | ASCII magic `CWL1` |
| 4 | 1 | record version (`1`) |
| 5 | 1 | record type (`1` Begin, `2` Put, `3` Delete, `4` Commit, `5` Abort) |
| 6 | 2 | flags (currently zero) |
| 8 | 2 | header size (`48`) |
| 10 | 2 | reserved (zero) |
| 12 | 8 | contiguous LSN |
| 20 | 16 | transaction GUID bytes |
| 36 | 4 | payload length |
| 40 | 4 | checksum algorithm (`1` = CRC32C) |
| 44 | 4 | CRC32C of the complete record with this field zeroed |

The record payload is limited to 65 MiB. Put values remain limited to 64 MiB; the extra envelope capacity guarantees room for the encoded key and length fields. A decoder requires exactly one complete record.

## Commit payload

The current Commit payload is 16 bytes:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | non-zero logical commit sequence |
| 8 | 8 | physical `chronicle.data` length before publication |

Recovery also accepts an 8-byte sequence-only development payload and an empty legacy v0.2 Commit payload.
