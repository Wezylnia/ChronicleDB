# Version Garbage Collection

`RunGarbageCollection` advances generic history floors and removes committed MVCC versions that are unreachable from every supported observer. Correctness has priority over reclamation rate.

## Protocol

For each selected history, under the history lifecycle gate and all relevant commit gates:

1. compute the target generic floor from the existing floor, current sequence, and `RetainRecentCommits`;
2. collect exact process-local transaction/read-handle boundaries and persistent roots protecting that history;
3. build the exact retained MVCC projection;
4. write and fsync a complete `chronicle.history` checkpoint;
5. publish the `HistoryCheckpointInitialized` capability flag when first introduced;
6. reset the history WAL only after the checkpoint is durable;
7. compact the managed MVCC version chains to the same projection;
8. advance the semantic history/snapshot floors;
9. reclaim branch-private directories for branches whose deletion is complete;
10. compact lifecycle journals to canonical active state.

The durable checkpoint precedes logical removal, so crash recovery never depends on versions that GC has already made unreachable.

## Crash outcomes

- crash before checkpoint capability publication: the old WAL remains complete and the unpublished checkpoint is ignored;
- crash after checkpoint publication but before WAL reset: recovery accepts the old WAL only if its final commit reaches the checkpoint boundary exactly;
- crash after WAL reset: checkpoint plus a WAL containing only strictly newer commits is authoritative;
- a WAL that mixes old-generation commits with post-reset commits, or reuses a checkpoint-retained transaction identity, is corruption.

A GC failure with uncertain persistence faults the open database; reopen is the recovery authority.

## Explainability and metrics

The history-root registry exposes the roots and protected histories that keep old state alive. Diagnostics record GC passes, reclaimed version count, checkpoint bytes, and elapsed time. These metrics never participate in liveness or correctness decisions.
