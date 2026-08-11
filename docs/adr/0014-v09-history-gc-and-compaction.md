# ADR 0014: v0.9 retained-history GC and copy-publish compaction

## Status

Accepted.

## Decision

ChronicleDB separates the generic time-travel floor from explicit historical roots. GC retains every version in the generic range plus the exact per-key versions needed by active readers, snapshots, and branch bases. Before WAL history is discarded, the retained projection is written as a complete identity-bound `chronicle.history` checkpoint.

Physical compaction is a separate copy-and-publish operation. A selected history receives a fresh checkpoint/WAL rotation before its data file is rewritten. Replacement output is fsynced and byte-for-byte validated before publication; old storage remains recoverable through a `.previous` file until publication completes.

## Consequences

- one very old branch does not automatically pin every unrelated intermediate version;
- GC remains recovery-safe after WAL rotation;
- compaction cannot depend on obsolete physical append offsets;
- maintenance passes can be throttled without changing semantics;
- v1.5 EBR/native reclamation is not required for v0.9 correctness.
