# ChronicleDB contributor rules

These rules apply to the entire repository.

1. Read `project-definition.md`, `ARCHITECTURE.md`, and the relevant local plan under `private-docs/` before changing engine behavior.
2. Preserve the source-project dependency DAG enforced by `ChronicleDB.ArchitectureTests`.
3. Put code in the owning feature folder. Do not create `Common`, `Helpers`, `Utils`, generic repository, generic service, or service-locator layers.
4. Keep `ChronicleDB.Core` dependency-free and small. Do not resolve cycles by moving unrelated types into Core.
5. Only `src/ChronicleDB` selects concrete implementations. Transaction, recovery, and maintenance code reference `Indexing.Abstractions`, never `Indexing.Baseline` or future optimized indexes.
6. Treat persistent layouts as protocols. Update format documentation, golden fixtures, corruption tests, and recovery tests with format changes.
7. Keep unsafe code disabled outside the approved native-memory project. Do not let pointers, epoch guards, pins, or borrowed spans escape their documented lifetime.
8. Extend the reference model before implementing changed logical semantics. Add fault injection for changed durable behavior.
9. Preserve simple baseline implementations when adding optimized variants. Validate both with identical deterministic workloads.
10. Run restore, build, architecture tests, and the affected correctness/recovery suites before declaring a change complete.
