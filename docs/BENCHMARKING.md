# v1.0 benchmarking methodology

ChronicleDB benchmarks are reproducible research instrumentation, not a claim that the project outperforms mature database engines. Correctness and durability gates take precedence over benchmark results.

## Runner

```powershell
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- <operations> <workers> [json-output] [seed]
```

The report records:

- ChronicleDB release label and Git commit hash (`CHRONICLEDB_COMMIT` can override automatic Git discovery);
- build configuration;
- UTC timestamp;
- operating system and process architecture;
- .NET runtime;
- logical CPU count;
- operation/worker counts and deterministic seed;
- page, key, and value sizes used by the built-in scenarios;
- durability mode;
- operations/second;
- P50/P95/P99 latency;
- allocated bytes and Gen0/Gen1/Gen2 collection deltas;
- scenario-specific WAL, history, root, branch, GC, compaction, recovery, and storage metrics.

Each scenario is invoked once for warm-up and then once for measurement. GC is explicitly collected between those invocations. Publishable experiments should run multiple independent processes and preserve every raw JSON file rather than treating one short invocation as evidence.

## v1.0 scenario matrix

- **Storage primitive write** — low-level append-oriented storage path; this is a microbenchmark, not the historical v0.5 B0 baseline.
- **B2 Main durable write** — current transactional/WAL durability path without user branches.
- **B2 Main concurrent write** — multiple independent writers through the semantic baseline commit coordinator.
- **current-state read** — latest committed Main state.
- **historical read** — fixed persistent snapshot boundary after later divergence.
- **snapshot create** — persistent metadata/root publication cost.
- **branch create** — creation of metadata-oriented fixed-base branches without copying Main user state; records metadata/private data/WAL growth.
- **branch inherited read** — local-miss resolution through the immutable parent base after Main diverges.
- **branch local write** — branch-local WAL + physical publication cost.
- **B3 branch-scale-1 / 10** — inherited reads with small active sibling sets.
- **B4 branch-scale-25 / 50 / 100** — the same shape at larger topology sizes to expose metadata and fallback-read overhead.
- **B6 GC pass** — retained-history checkpoint/WAL rotation and managed version reclamation.
- **B8 compaction pass** — checkpoint-before-rewrite plus physical copy/publish reclamation.
- **recovery open** — reopen of Main plus multiple branches and persistent Main/branch snapshots.

The labels B2/B3/B4/B6/B8 correspond to current-tree v1.0 baseline families. **B0 means the actual v0.5 release** and is intentionally not fabricated inside the v1.0 executable; historical-release comparisons must check out the tagged v0.5 revision and run an equivalent workload with the same durability and machine settings. The low-level storage primitive microbenchmark is reported separately.

## Required interpretations

Branch creation must be described as **metadata-oriented / shared-state / without full logical database duplication** unless measured evidence justifies a stronger statement. Do not claim "zero-cost", "instant", or `O(1)` branch creation from one fixed-size microbenchmark.

GC results must report both effectiveness and interference. Compaction results must include bytes rewritten as well as bytes reclaimed. Recovery timing is meaningful only when the reopened Main, branches, ancestry, and persistent snapshots are also validated.

The commit coordinator and conventional managed synchronization are intentional v1.0 semantic baselines. v1.5 optimizations should be compared against this release with unchanged logical semantics and equivalent durability.

See [RESEARCH_EVALUATION.md](RESEARCH_EVALUATION.md) for research questions and the metadata required for publication-quality experiments.
