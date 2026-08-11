# Physical compaction

Garbage collection decides **what historical state is logically unnecessary**. Compaction decides **how the surviving physical state is rewritten**. The operations are deliberately separate.

## Recovery prerequisite

A physical rewrite invalidates append-length recovery bases recorded by older WAL commits. Before rewriting a selected history, ChronicleDB therefore refreshes a complete retained-history checkpoint and resets that history's WAL. Recovery no longer depends on the physical layout being replaced.

## Copy and publish

`PersistentKeyValueStore.RewriteState` never destructively edits the authoritative data file in place:

1. build a complete replacement in a temporary same-directory store;
2. validate all deterministic limits;
3. fsync the replacement;
4. reopen it and compare every key/value byte against the requested logical state;
5. move the old `chronicle.data` to `.previous`;
6. publish the replacement as `chronicle.data`;
7. reopen and validate the published file again;
8. retire `.previous` only after successful publication.

If the process dies between publication renames, open can restore the old file or accept the complete new file. When both `chronicle.data` and `.previous` exist, the previous generation is not discarded until the published primary passes storage framing/checksum validation; a torn/corrupt primary restores `.previous`. For branch data, a crash after file publication but before branch physical-boundary metadata is repaired only after the accepted new file is proven equivalent to authoritative checkpoint/WAL history.

## Granularity and throttling

v1.0 compacts whole physical history files, but one maintenance pass is incremental across histories. Candidate selection uses the exact page/overflow size that the replacement would occupy, not a key/value byte estimate. Options bound:

- maximum histories per pass;
- minimum reclaimable bytes;
- maximum bytes rewritten per pass.

A history that already has its exact compact representation is not rewritten again.

## Observational equivalence

Before and after compaction, Main current state, retained Main snapshots, branch current state, branch snapshots, and retained historical reads must return identical values. Differential tests repeat this comparison after restart against the independent reference model.
