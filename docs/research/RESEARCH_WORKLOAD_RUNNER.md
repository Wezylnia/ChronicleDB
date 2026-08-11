# Research baseline workload runner

`ChronicleDB.ResearchWorkloadRunner` executes deterministic S0-S4 and S6 logical
workloads against the v1.0 baseline engine. Each run has two separate directories:

```text
<output>/
  database/       ChronicleDB persistent state
  artifacts/      manifest, workload, crash plan, and trace artefacts
```

Example:

```text
dotnet run --project tools/ChronicleDB.ResearchWorkloadRunner \
  --configuration Release -- S1 17 1000 C:\\research\\runs\\s1-17
```

The runner writes `manifest.json`, `workload.json`, `crash-plan.json`, and `trace.json`,
each with a lower-case SHA-256 sidecar. It validates the trace lifecycle and dependency
protocol before reporting `PASS`. The logical workload, manifest, crash-plan, and trace
hashes are printed in the result line and can be copied into a campaign index.

S0-S4 and S6 are enabled because their generated branch/read/write/snapshot
operations have a direct baseline mapping. S5 and S7 are rejected explicitly until
their crash-process semantics are implemented. S6 is executable here as a baseline
erasure-conflict workload, but this runner does not claim secure deletion or erasure
correctness. A rejected family is not a negative research result and must not be
included as a completed pilot.
