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

Crash/recovery smoke:

```text
dotnet run --project tools/ChronicleDB.ResearchWorkloadRunner \
  --configuration Release -- crash S5 7 200 C:\\research\\runs\\s5-crash-7
```

Crash mode starts a child process, applies the first deterministic crash-plan
injection, waits for the fail-fast exit, and reopens the database in the parent. The
resulting `trace.json` is explicitly a post-crash recovery trace: it proves recovery
milestones for that run but is not a complete pre-crash execution trace. A complete
POR campaign still needs durable event streaming or a separate pre-crash trace channel.

The runner writes `manifest.json`, `workload.json`, `crash-plan.json`, and `trace.json`,
each with a lower-case SHA-256 sidecar. It validates the trace lifecycle and dependency
protocol before reporting `PASS`. The logical workload, manifest, crash-plan, and trace
hashes are printed in the result line and can be copied into a campaign index.

S0-S4 and S6 are enabled because their generated branch/read/write/snapshot
operations have a direct baseline mapping. S5 and S7 are rejected explicitly until
their normal (non-crash) execution semantics are implemented. S5 and S7 crash mode
are available for the first deterministic injection/recovery smoke. S6 is executable as a baseline
erasure-conflict workload, but this runner does not claim secure deletion or erasure
correctness. A rejected family is not a negative research result and must not be
included as a completed pilot.
