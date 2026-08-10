# ADR 0010: v0.5 persistence lifecycle and framing hardening

## Status

Accepted.

## Context

Earlier development formats had two lifecycle ambiguities:

1. a missing WAL could look identical to a database that had never initialized WAL, allowing accidental history reset during upgrade;
2. a corrupted final WAL payload-length field could sometimes be mistaken for an ordinary incomplete tail before the record checksum was reachable.

v0.5 also adds a second critical persistent stream for named snapshots, so the same mistakes must not be repeated there.

## Decision

### Database metadata journal

Keep the 64-byte database-header slot but move current metadata to minor version 1.1. `chronicle.meta` becomes an append-only journal of complete checksummed slots. Current slots add a strictly increasing generation in the former reserved region and monotonic capability flags for WAL and persistent-snapshot initialization.

Legacy 1.0 slots remain readable only with zero flags/reserved bytes. The first capability update appends a 1.1 generation rather than rewriting the only durable identity record in place.

A partial final slot may be removed; a corrupt complete slot is fatal. Once a capability flag is durable it can never be removed by a successor generation.

### WAL record version 2

Keep the WAL file header at 1.0 for compatibility. New WAL records use record version 2 and replace the legacy per-record checksum-algorithm slot with the bitwise complement of payload length. CRC32C is fixed by v2. Readers continue to accept v1 records.

The scanner validates the complete fixed header, including redundant length, before classifying an incomplete payload as a crash tail.

### Persistent snapshot framing

Snapshot lifecycle metadata uses its own database-bound header plus checksummed records with redundant total length in the header and footer. Only an incomplete final frame can be automatically truncated.

## Consequences

- deleting an already-initialized WAL or snapshot registry becomes detected corruption rather than silent empty recreation;
- once WAL initialization is durable, physical keys outside WAL-backed history are detected instead of adopted as a later synthetic upgrade;
- upgrades are append-only at the database-metadata level;
- framing corruption has a smaller chance of being confused with a crash tail;
- persistent formats remain backward-readable for the explicitly supported v1.0/v1 record forms;
- future format changes must preserve or deliberately migrate these lifecycle invariants.
