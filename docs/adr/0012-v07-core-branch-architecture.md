# ADR 0012: v0.7 core branch architecture

- Status: Accepted
- Release: v0.7

## Context

v0.6 generalized persistent snapshots into history roots. v0.7 must make a retained historical point independently writable without copying the complete parent database, while preserving Snapshot Isolation, tombstone semantics, historical determinism, and source-snapshot independence. Full branch WAL/recovery is reserved for v0.8.

## Decision

Each branch receives a persistent `BranchId`, an independent `HistoryId`, a fixed parent `(HistoryId, CommitSequence)` base, an active `BranchBase` retention root, and an append-only branch-private version store.

Sequences are per history domain. Branch reads first resolve local MVCC state at a branch-local sequence; only the absence of a visible local version falls back to the fixed parent base. A local tombstone terminates lookup as absent.

Branch creation is metadata-oriented and does not copy inherited records. `chronicle.branches` journals CreateIntent, Activate, local sequence publication, and abandoned creation. A local sequence publication also records the exact append prefix that is authoritative for v0.7 reopen. Per-branch conventional commit gates protect first-committer-wins and ordered local publication.

Nested branching is supported recursively with an explicit depth limit of 16.

## Consequences

- Main and sibling histories evolve without cross-history write conflicts.
- Branch creation cost is dominated by metadata and empty local-store initialization rather than dataset copying.
- Source snapshot deletion does not invalidate an activated branch because the branch owns an independent base retention root.
- Local deletes must remain explicit tombstones; removing the local record would incorrectly expose inherited state.
- v0.7 can reconstruct its committed local prefix after reopen, but does not claim the independent branch-WAL protocol, branch deletion lifecycle, or v0.8 crash matrix.
- Recursive parent fallback is intentionally simple and bounded; flattening/caching ancestry is a later optimization.
