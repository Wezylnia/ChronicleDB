# ADR 0003: Persistent format governance

- Status: Accepted
- Date: 2026-08-10

## Context

Database pages, headers, checkpoints, WAL records, snapshots, and branch metadata must survive crashes and future software versions. Ordinary refactoring rules are insufficient for persisted bytes.

## Decision

Treat every persisted binary layout as a versioned protocol. The owning persistence project defines the codec, validation, checksum scope, limits, fixtures, corruption behavior, and compatibility policy. Recovery and tools reuse those codecs rather than duplicating them.

Incompatible changes require a new ADR and a migration or an explicit unsupported-version failure.

## Consequences

- Format changes carry documentation and golden-fixture cost.
- Recovery behavior stays aligned with writers.
- Tools cannot silently become an alternative parser with different safety rules.
