# ADR 0004: Unsafe/native code containment

- Status: Accepted
- Date: 2026-08-10

## Context

v1.5 may use native memory, borrowed views, epoch-based reclamation, and latch-free index publication. Enabling unsafe code across the solution would make ownership review and lifetime testing impractical.

## Decision

Disable unsafe code globally. After the v1.1 ownership/profiling gate, a dedicated `ChronicleDB.Memory.Native` project may opt in. Epoch reclamation and latch-free indexing receive separate projects and depend on the native-memory contract without exporting raw pointers to semantic or public API assemblies.

Every native allocation requires documented allocation, ownership, publication, retirement, protection, and free states.

## Consequences

- Native optimization requires explicit project and architecture-test changes.
- Safe projects cannot accumulate incidental pointer-based shortcuts.
- Cross-assembly APIs use safe handles or scoped abstractions rather than naked addresses.
