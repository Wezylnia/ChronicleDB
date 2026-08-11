# Research Evaluation Contract

ChronicleDB v1.0 is an experimental system. Evaluation must distinguish **measured evidence** from architectural intent. The repository provides reproducible workloads and metrics; it does not embed a preselected conclusion.

## Questions

### RQ1 — Snapshot creation cost

Measure persistent snapshot latency while varying database size, retained version count, and existing root count. The hypothesis to test is that snapshot creation is metadata-oriented rather than proportional to copying the logical database.

### RQ2 — Branch creation and sharing cost

Measure branch creation latency while varying parent database size, parent history age/depth, and existing branch/root count. Record branch metadata bytes, branch-private data bytes, branch WAL bytes, and unchanged Main data bytes. Do not label the operation "zero cost" or `O(1)` without a separate asymptotic experiment that justifies that claim.

### RQ3 — Branch runtime overhead

Compare current Main reads/writes, inherited branch reads, branch-local writes, and workloads with 0/10/50+ branches. Record P50/P95/P99 latency, throughput, allocation/GC activity, WAL bytes, and branch-private storage.

### RQ4 — Retention and GC

Vary snapshot age/count, branch age/count, and history depth. Record versions before/after GC, reclaimed versions, checkpoint bytes, retention floors, foreground throughput, and tail latency when running longer external experiments.

### RQ5 — Compaction

Measure physical bytes before/after compaction, bytes rewritten/reclaimed, pass duration, and foreground interference. Logical state must be compared before and after through Main, branch, and retained snapshot observers.

### RQ6 — Recovery

Measure reopen time as a function of Main WAL size, branch count, branch WAL size, persistent snapshot count, and checkpoint age. Validate recovered logical state; process startup alone is not a correctness oracle.

## Required experiment metadata

Preserve, at minimum:

- repository commit hash (the benchmark runner records `CHRONICLEDB_COMMIT` when set, otherwise attempts `git rev-parse HEAD`);
- ChronicleDB release/version label;
- UTC timestamp;
- OS and architecture;
- .NET runtime and SDK;
- CPU/logical processor count;
- storage device/filesystem when reporting I/O results;
- Release/Debug build configuration;
- page size;
- key/value sizes;
- operation count and worker count;
- deterministic workload seed;
- branch and snapshot counts;
- durability behavior;
- GC/compaction configuration;
- raw per-run JSON and unaggregated logs where practical.

## Repository runners

Deterministic multi-history correctness/soak runner:

```powershell
dotnet run -c Release --project tools/ChronicleDB.WorkloadRunner -- 42 10000 8
```

Process-level crash campaign:

```powershell
dotnet run -c Release --project tools/ChronicleDB.CrashHarness -- run 100
```

v1.0 research benchmark suite:

```powershell
dotnet run -c Release --project benchmarks/ChronicleDB.Benchmarks -- 1000 8 .artifacts/benchmarks/v10.json 42
```

The benchmark executable performs a warm-up run before each measured run. For publishable evaluation, execute multiple independent measured processes, retain every raw output, report distributions/confidence intervals, and explain outlier handling rather than discarding inconvenient runs.

## Correctness before performance

A result is not eligible for performance interpretation if the corresponding correctness/recovery gate fails. In particular, a benchmark configuration must not improve results by disabling the durability barrier, deleting retained history, bypassing WAL/checkpoint validation, or weakening snapshot/branch semantics unless that configuration is explicitly presented as a different system variant.
