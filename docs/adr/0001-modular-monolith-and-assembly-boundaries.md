# ADR 0001: Modular monolith and assembly boundaries

- Status: Accepted
- Date: 2026-08-10

## Context

ChronicleDB is expected to grow while spanning transactional semantics, persistent formats, recovery, historical roots, maintenance, native memory, and experimental concurrent indexes. A single project would make forbidden dependencies invisible. One project per feature would create high fan-out and assembly ceremony without stronger ownership.

## Decision

Use one repository and one embedded engine distribution with assemblies at real correctness, dependency, replacement, unsafe-code, or executable boundaries. Keep tightly coupled semantic features together and organize them with feature folders. Make `ChronicleDB` the only runtime composition root.

The exact source-project graph is enforced by architecture tests.

## Consequences

- Incorrect dependency direction fails in tests.
- Project count remains meaningful rather than mirroring every folder.
- Some assemblies contain several internal features; their ownership is documented in `ARCHITECTURE.md`.
- Splitting or merging projects requires an ADR and architecture-test update.
