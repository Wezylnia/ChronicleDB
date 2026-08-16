# ChronicleDB Contributor Guide

These rules apply to the entire repository.

1. Read `ARCHITECTURE.md` and the affected architecture-topic documents before changing engine behavior. When available locally, also consult the internal scope and ADR records under `private-docs/`.
2. Preserve the project-reference DAG enforced by `ChronicleDB.ArchitectureTests`; do not resolve dependency cycles by moving unrelated code into `ChronicleDB.Core`.
3. Place code in the assembly that owns the invariant. Avoid catch-all `Common`, `Utils`, service-locator, generic repository, or generic service layers.
4. Only the `ChronicleDB` composition root selects concrete replaceable implementations. Engine semantics depend on stable abstractions, not the managed baseline index or future optimized index types.
5. Treat every persistent layout as a protocol. Format changes require documentation, compatibility analysis, corruption coverage, and recovery tests.
6. Keep unsafe code disabled in v1.0. Future native-memory work requires an explicit ownership contract for allocation, publication, protection, retirement, and reclamation.
7. Extend the reference model before changing logical semantics. Add fault injection when a change alters persistence or crash behavior.
8. Do not weaken durability, retention, or validation to improve a benchmark. Performance work must preserve the same semantic configuration.
9. Keep baseline implementations available when optimized variants are introduced so differential validation remains possible.
10. Before declaring a change complete, run restore/build, the architecture suite, affected unit/persistence/correctness/recovery tests, and the relevant crash/workload campaign.

Security-sensitive changes should also update `docs/SECURITY.md`. A checksum is never a substitute for cryptographic authenticity, and best-effort cleanup must not be confused with logical publication.
