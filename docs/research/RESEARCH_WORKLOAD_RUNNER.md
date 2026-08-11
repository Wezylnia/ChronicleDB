# Research baseline workload runner

`ChronicleDB.ResearchWorkloadRunner` executes the deterministic S0–S3 logical
workloads against the v1.0 baseline engine. Each run has two separate directories:

```text
<output>/
  database/       ChronicleDB persistent state
  artifacts/      manifest, workload, and trace artefacts
```

Example:

```text
dotnet run --project tools/ChronicleDB.ResearchWorkloadRunner \
  --configuration Release -- S1 17 1000 C:\\research\\runs\\s1-17
```

The runner writes `manifest.json`, `workload.json`, and `trace.json`, each with a
lower-case SHA-256 sidecar. It validates the trace lifecycle and dependency protocol
before reporting `PASS`. The logical workload, manifest, and trace hashes are printed
in the result line and can be copied into a campaign index.

S0–S3 are currently enabled because their generated branch/read/write/snapshot
operations have a direct baseline mapping. S4–S7 are rejected explicitly until their
independent-history, crash-process, and erasure semantics are implemented. A rejected
family is not a negative research result and must not be included as a completed pilot.
