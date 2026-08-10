# ADR 0005: v0.1 storage format

- Status: Accepted
- Date: 2026-08-10

## Context

v0.1 must prove persistence without transactions or WAL. The format must be deterministic, explicitly encoded, bounded, checksummed, and simple enough to inspect and recover. It must also leave room for later MVCC records without pretending that page bytes are transactionally durable before WAL exists.

## Decision

The v0.1 database directory contains:

- `chronicle.meta`: one fixed 64-byte database header;
- `chronicle.data`: an append-only sequence of fixed 16 KiB pages.

All integers are little-endian with explicit widths. The header and every page are checksummed with CRC32C. Page IDs are one-based; zero is the invalid/sentinel page ID. A page is never rewritten by the v0.1 store.

Each logical record occupies one data page. Values that do not fit in the record page use append-only overflow pages linked by one-based page IDs. The in-memory baseline index points to the newest record page for each full binary key and is rebuilt by scanning pages on open. A tombstone record removes a key from the current index while preserving the append-only history in the file.

The format is not a WAL and does not claim crash-atomic `Put` or `Delete`. A failed or interrupted append may leave unreachable pages; a complete page is either validated and replayed during open or causes deterministic corruption failure. WAL-backed atomic transactions begin in v0.2.

## Consequences

- The first implementation is easy to inspect and differentially test.
- Reopen cost is linear in page count until a later checkpoint/index format is introduced.
- Orphan pages are tolerated as unreachable physical history but are not reclaimed in v0.1.
- A future WAL/checkpoint format can be introduced without changing the page codec contract.
- Changing field offsets, checksum scope, page size, or page type requires a new format version and migration decision.
