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
