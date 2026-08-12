# ChronicleDB v1.1 Research Baseline

Bu belge v1.1 araştırma harness'ının v1.0 baseline sözleşmesidir. Baseline semantic authority değildir; yalnız yeniden üretilebilir karşılaştırma referansıdır.

## Baseline identity

Baseline artifact aşağıdakiler birlikte saklanmadan geçerli sayılmaz:

```text
ChronicleDB version
Git commit
Build configuration
.NET SDK/runtime
OS/architecture
CPU/memory
Storage device/filesystem
Workload generator version
ManifestFormatVersion
ResearchTraceFormatVersion
Reference-model result
Artifact hashes
```

Baseline executable, config ve raw results ölçüm başladıktan sonra in-place değiştirilemez. Candidate-specific mechanism baseline executable'a veya default semantic configuration'a giremez.

## Required competitor baselines

| Candidate | Baselines |
| --- | --- |
| 1 | global/coarse horizon; per-root exact; ZFS/NetApp-style physical abstraction |
| 9 | exhaustive; random; phase-only; resource-only; strong resource+dependency POR; declared failure model'e uyarlanmış history/topology reduction |
| 2 | recursive fallback; memoization; eager flattened/materialized routing |
| 17 | recover-all; requested replay first; dependency-closure first |
| 8 | tombstone/current-state delete; rewrite-all; closure-aware plan |
| 10 | monolithic specification; generic composition; ChronicleDB-specific contracts |

Bir uyarlama tam reimplementation değilse hangi özelliği temsil ettiği ve hangi farkı koruyamadığı sonuç raporunda yazılır.

## Run identity

Her raw result manifest ile birlikte şu alanları taşır:

```text
ExperimentId
WorkloadSeed
CrashPlanSeed
MutationSeed
ProcessRepetition
MachineBlock
TrialOrder
CandidateMode
CandidateConfigHash
TelemetryMode
FailureModelVersion
NoveltyCardVersion
```

Pilot-A, sealed Holdout-A ve sealed Holdout-B birbirinden ayrıdır. Correctness bug içeren Holdout-A run'ı publication evidence değildir; düzeltme sonrası daha önce açılmamış Holdout-B kullanılır.

## Current falsification seams

- `P1IR` repeats the P1 interference workload in separate child processes and writes a deterministic trial plan before execution. Publication campaigns should prefer this over a single `P1I` process when reporting tail-latency interference.
- `P2A` records real branch create lifecycle operations and compares the proposed history-aware relation against the strong generic resource+dependency relation. `TopologyContributionObserved=false` is a valid weakening result, not a harness failure.
- A Candidate 9 claim must not attribute reduction to history topology when the strong generic resource+dependency baseline produces the same independence relation and reduced crash-plan count.

## Candidate repeated campaigns and gate closure

`P3BR` repeats the stable ancestry-routing prototype in separate child processes. It writes a deterministic trial plan before execution and aggregates P99/mean speedup together with reopen, compaction, route-hit and invalidation correctness. Use it instead of a single `P3B` run when arguing repeatability; a low minimum or wide P05-P95 range is falsification evidence and must not be discarded.

```powershell
dotnet run -c Release --project tools/ChronicleDB.ResearchWorkloadRunner -- \
  pilot P3BR 301 3 3 8 64 10000 uniform .artifacts/p3br
```

The research runner also exposes an immutable gate-artifact path:

```powershell
dotnet run -c Release --project tools/ChronicleDB.ResearchWorkloadRunner -- \
  gate decision A9 weakened a9-resource-authority-v2 rationale.txt candidate-a9.json p2a-result.json

dotnet run -c Release --project tools/ChronicleDB.ResearchWorkloadRunner -- \
  gate report v1.1 .artifacts/research-gate candidate-a1.json candidate-a2.json candidate-a9.json
```

A gate decision hashes every cited evidence artifact, requires a non-empty narrow-claim version and rationale, and is written with create-new semantics. `ResearchGateReport` canonicalizes candidate ordering and rejects duplicate candidate decisions. The report is an evidence ledger, not an automatic ranking or paper-selection algorithm.

A smoke/pilot gate report is not a publication holdout. Final paper claims still require the preregistered Pilot-A / sealed Holdout-A / sealed Holdout-B protocol, multiple process repetitions, retained raw outputs and the stated machine-block policy.

### Sealed A1 holdout protocol

`P1H` is the executable preregistration path for the current A1 primary track. It creates the candidate configuration, every Holdout-A and Holdout-B `ExperimentManifest`, and the combined `ResearchCampaignRegistration` **before the first Holdout-A child process is started**. Only Holdout-A executes; Holdout-B remains sealed and untouched unless a correctness-invalid Holdout-A requires the predeclared fallback.

```powershell
dotnet run -c Release --project tools/ChronicleDB.ResearchWorkloadRunner -- \
  pilot P1H 1101 2101 5 3 128 4096 4 16 4096 20000 machine-block-a .artifacts/a1-holdout
```

The parent runner constructs child arguments from the sealed plan and verifies identity fields in each `P1I` result against the sealed configuration. Each execution records the manifest SHA-256 and result SHA-256. A `P1H` smoke proves the protocol implementation, not the paper result: publication use still requires the final frozen A1 parameter matrix and declared machine blocks.
