# Deterministic S0–S7 workload inputs

`DeterministicResearchWorkloadGenerator` defines the version-one logical input
families used by the novelty-strike pilots. It does not execute engine commands and
does not decide durability, recovery, retention, or publication semantics. A runner
maps the operations to a selected baseline or candidate implementation.

The generator is identified by `GeneratorFormatVersion = 1` and uses a local
xorshift32 PRNG. Consequently, a workload is reproducible from the tuple
`(generator format, family, seed, operation count)` without depending on the runtime's
`System.Random` implementation. The workload seed belongs in the experiment manifest.

The eight families intentionally expose different stressors:

| Family | Shape | Primary targets |
| --- | --- | --- |
| S0 | shallow control workload | control measurements |
| S1 | old base with a thin branch and parent churn | marginal retention |
| S2 | overlapping branch/snapshot roots | retention and erasure closure |
| S3 | progressively deep inherited reads | ancestry routing/indexing |
| S4 | many independent sibling histories | multi-log persistence ordering |
| S5 | crash/recovery-heavy requested-history workload | readiness scheduling and recovery proofs |
| S6 | parent/snapshot/overwrite/tombstone/branch conflict | erasure contract |
| S7 | mixed writes, branches, snapshots, GC, compaction, and crashes | adversarial cross-candidate soak |

The generated sequence is an input artifact, not evidence of a candidate's
correctness. Runs must preserve the family, seed, count, generator version, and
manifest hash beside their raw result.
