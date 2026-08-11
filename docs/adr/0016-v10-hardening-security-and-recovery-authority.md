# ADR 0016: v1.0 hardening, security boundaries, and recovery authority

- **Status:** Accepted
- **Release:** v1.0 hardening

## Context

The v1.0 semantic freeze established the transaction, history, branch, GC, and compaction model. A subsequent adversarial review focused on failure classification, resource usage, cleanup semantics, and externally visible tooling without changing those semantics.

Several implementation details required tightening: deterministic repeated key hashing was unnecessary on hot lookup paths; a complete branch metadata frame could be misclassified as a crash tail when only its declared length was corrupt; checkpoint backup cleanup was coupled too closely to primary validation; and best-effort deletion of stale generations could turn successful physical publication into an availability failure.

## Decision

1. `BinaryKey` computes and caches a process-seeded hash when the immutable key is created. Structural byte equality remains authoritative.
2. Persistent branch metadata uses the same complete-footer distinction as snapshot metadata: a complete record whose header length disagrees with the physical frame is corruption, not a truncatable tail.
3. A validated primary history checkpoint remains authoritative even if deletion of `.previous` fails. A `.previous` checkpoint is restored only when the canonical primary is missing after an interrupted publication rename. A present but invalid primary fails closed rather than rolling history back to an older generation.
4. Once a replacement physical or metadata generation has been validated and reopened, stale backup/temp deletion is best-effort cleanup rather than a correctness decision.
5. Persistent names remain format-compatible, but terminal-facing tooling escapes terminal control and Unicode format characters before display.
6. Maintenance canonical ordering compares raw binary keys directly instead of allocating hexadecimal string representations.

## Consequences

- Hot key lookup avoids repeated full-key hashing while remaining collision-safe.
- Crash-tail repair is less likely to hide durable metadata corruption.
- Filesystem cleanup failures cannot silently roll recovery back to an older validated checkpoint.
- Successful publication is not reported as a logical database failure solely because stale recovery evidence cannot be deleted immediately.
- Security documentation distinguishes accidental-corruption detection from cryptographic authenticity and places directory access control at the host/OS boundary.
