# v0.5 benchmarking methodology

v0.5 benchmarks establish reproducible baselines; they are not a claim that ChronicleDB outperforms mature engines.

## Runner

```powershell
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- <operations> <workers> [json-output]
```

The runner records:

- UTC timestamp;
- operating system;
- process architecture;
- .NET runtime;
- logical CPU count;
- operation and worker counts;
- operations/second;
- P50/P95/P99 latency;
- allocated bytes and Gen0/Gen1/Gen2 collection deltas;
- subsystem-specific WAL, commit, index, version-chain, snapshot, recovery, and storage metrics.

A warm-up invocation precedes the measured invocation. GC is collected between warm-up and measurement to reduce startup noise. Raw JSON should be preserved for research comparisons.

## Current baseline cases

- **B0 persistent KV write** — low-level append-only storage path;
- **B2 MVCC durable write** — single-worker current v0.5 transactional path;
- **B3 concurrent MVCC** — multiple independent writers through the v0.5 commit coordinator;
- **B4 current-state read** — committed current boundary;
- **B4 historical read** — fixed persistent snapshot boundary after later divergence;
- **snapshot create** — metadata durability cost;
- **recovery open** — reopen/replay cost after a configured committed history.

The original v0.2 transactional implementation is not maintained as a runtime-selectable production implementation. Historical B1 comparisons should therefore use the relevant tagged revision rather than duplicate an obsolete commit protocol inside benchmark code.

## Interpretation

The commit coordinator intentionally serializes durability ordering. Contention counters make that cost visible rather than hiding it. A future optimized implementation should be compared against this semantic baseline using the same workload shape and durability mode.

Do not compare runs unless machine, storage, runtime, build configuration, workload parameters, durability behavior, and background conditions are recorded. Prefer multiple sufficiently long runs and report raw distributions; one short run is not research evidence.

## Checkpoint/recovery note

v0.5 keeps historical WAL because it is required to rebuild retained version chains. No benchmark is allowed to improve recovery time by truncating history and silently weakening snapshots/time travel. Checkpointing can be added only when an equivalent durable historical representation exists.
