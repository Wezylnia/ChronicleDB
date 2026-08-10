# v0.2 WAL record format

Every WAL record is encoded in little-endian form.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | ASCII magic `CWL1` |
| 4 | 1 | record version (`1`) |
| 5 | 1 | record type (`1` Begin, `2` Put, `3` Delete, `4` Commit, `5` Abort) |
| 6 | 2 | flags (currently zero) |
| 8 | 2 | header size (`48`) |
| 10 | 2 | reserved (zero) |
| 12 | 8 | monotonically allocated LSN |
| 20 | 16 | transaction GUID bytes |
| 36 | 4 | payload length |
| 40 | 4 | checksum algorithm (`1` = CRC32C) |
| 44 | 4 | CRC32C of the complete record with this field zeroed |

The payload is limited to 64 MiB. A decoder requires the byte span to contain exactly one complete record; a file scanner may apply a separate valid-tail policy when EOF occurs in the final record.
