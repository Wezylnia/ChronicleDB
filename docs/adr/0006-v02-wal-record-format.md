# ADR 0006: v0.2 WAL record format

- Status: Accepted
- Date: 2026-08-10

## Decision

The v0.2 WAL uses independently checksummed, versioned records. Each record has a 48-byte little-endian header followed by a bounded payload.

The header contains `CWL1` magic, version, record type, flags, header size, LSN, transaction ID, payload length, checksum algorithm, and CRC32C. The checksum covers the complete record with its checksum field zeroed.

The initial record types are Begin, Put, Delete, Commit, and Abort. Flags and reserved fields are zero in v0.2. The maximum payload is 64 MiB and all length arithmetic is checked before allocation.

A codec rejects unsupported versions, record types, algorithms, flags, malformed lengths, invalid identities, and checksum failures. File scanning and tail policy are deliberately separate from this byte-level contract.
