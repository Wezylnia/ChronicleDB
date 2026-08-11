# Benchmarks

The benchmark project measures ChronicleDB behavior under reproducible local workloads. It records throughput, latency percentiles, allocations, GC activity, environment metadata, and scenario-specific storage/WAL/history metrics.

Benchmarks are not correctness or release oracles. A result is meaningful only after the corresponding correctness and recovery gates pass, and benchmark-only shortcuts are not permitted in production paths.

See `docs/BENCHMARKING.md` and `docs/RESEARCH_EVALUATION.md` for scenario definitions and publication requirements.
