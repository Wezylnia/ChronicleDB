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

Run every crash injection in a generated plan:

```text
dotnet run --project tools/ChronicleDB.ResearchWorkloadRunner \
  --configuration Release -- campaign S5 7 200 C:\\research\\runs\\s5-campaign-7
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

## Focused v1.1 pilot examples

Repeat P1 foreground-interference measurements in independent child processes:

```text
dotnet run --project tools/ChronicleDB.ResearchWorkloadRunner --configuration Release -- \
  pilot P1IR 100 5 5 256 4096 32 32 512 30000 C:\research\p1ir
```

The runner writes `p1ir-plan.json` before executing the shuffled seed/repetition order and
aggregates P95/P99 interference plus reclamation work in `p1ir-result.json`.

Attack the Candidate 9 topology claim with real branch lifecycle traces:

```text
dotnet run --project tools/ChronicleDB.ResearchWorkloadRunner --configuration Release -- \
  pilot P2A 3 siblings C:\research\p2a-siblings
```

`P2A` compares the proposed relation with resource-only and strong resource+dependency
baselines. A zero relation difference is preserved as negative novelty evidence; the
pilot passes when the bounded observer-equivalence checks are sound, not when topology
necessarily improves reduction.

## Repeated ancestry and research-gate commands

`P3BR` runs `P3B` in independent child processes across deterministic seed/repetition order and writes `p3br-plan.json` before any child result is observed. This prevents a single warm process from defining the ancestry performance claim.

The top-level `gate` command records candidate dispositions from evidence-file SHA-256 hashes and aggregates them into a canonical research-gate report. These artifacts are research metadata only; they never participate in engine recovery, retention, routing or erasure authority.

`P1H` seals both A/B retention holdout partitions before opening A. It is intentionally candidate-specific rather than a generic benchmark wrapper so the A1 configuration fields and returned `P1I` identity can be checked against the preregistered manifests. Holdout-B is not run by this command.
