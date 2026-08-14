# External Backend Provisioning Note

This note records the prerequisites that are intentionally missing from the current Windows host. The campaigns were invoked independently and are not treated as failed semantic experiments.

## Host observations

Recorded in [`artifacts/environment/environment.json`](../../artifacts/environment/environment.json):

| Dependency | Observation | Blocked campaigns |
|---|---|---|
| Docker Engine / CLI | `docker` command not found | MatrixOne container and image-digest capture |
| MySQL-compatible client | `mysql` command not found | MatrixOne continuation, identity, and budget probes |
| Rust + Cargo | `rustc` and `cargo` not found | SlateDB 0.14.1 observer and budget probes |
| Go | `go` command not found | pinned current-Dolt-main build/control |
| Dolt CLI | `dolt` command not found | Dolt 2.2.3/2.3.0 budget and clone-smoke probes |

## Recorded command outcomes

The seven external modes returned harness-unavailable exit code `3` and their stderr is retained under `artifacts/baseline/external/`:

```text
matrixone
matrixone-identity
matrixone-budget
slatedb
slatedb-budget
dolt-budget
dolt-clone-smoke
```

The MatrixOne modes failed before a SQL request because `mysql` was unavailable. SlateDB modes rejected the absent `BRANCHCHECK_SLATEDB_PROBE`. Dolt modes could not start the missing executable.

## Provisioning contract for resumption

Use a Linux runner or WSL2/Docker host and pin all identities before execution:

1. MatrixOne image digest and exact client version.
2. SlateDB crate `0.14.1` and the fixed commit used by the paired probe, with generated `Cargo.lock` committed to the artifact directory.
3. Dolt release binaries `2.2.3` and `2.3.0`, plus the current-main commit SHA and Go toolchain used for the causal control.
4. The same BranchCheck commit, candidate grammar fingerprint, seed set, timeout, and operation budget as the local provenance table.

Do not replace these with `latest` images or unpinned source builds. Once provisioned, rerun the same command modes and append results rather than overwriting the unavailable logs.
