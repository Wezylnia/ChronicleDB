# A1 Shadow-Aware Retention — Pilot-A Results

Status date: 2026-08-12

This document freezes the **Pilot-A** outcome for the post-novelty-attack A1 candidate. It is not Holdout-A and must not be presented as publication holdout evidence.

## Sealed provenance

- Publication plan format: v3.
- Publication plan SHA-256: `5906da5feaed5ed85c3926ee38232a6aeb708a8aaf8d02f6e942bcb6a3e24302`.
- Pilot-A execution plan SHA-256: `c410d6f20e93ad288e7f3f8cc87b58c6c4cc803826d97a81c426a5c2f3f425f2`.
- Execution-plan file SHA-256 (serialized artifact bytes): `6c9a12b81494d28d1a0ba25a14f213767242bcc9735e80c79c3203e8a0697155`.
- Pilot-A aggregate result file SHA-256: `b222310b9f19c22055eac6ae684429650a012a0c3c4bd0c5045a1eb52ca52097`.
- Canonical sensitivity sweep: 158 runs.
- Independent-process repeated sentinels: 135 runs.
- Total preregistered Pilot-A runs: **293**.
- Executed: **293 / 293**.
- Failures: **0**.

The execution order was generated and sealed before result inspection from the publication-plan hash plus each run identity. Every child result was checked against its sealed topology, key count, branch/depth, shadow fraction, tombstone fraction and seed.

## Correctness/falsification gates

Across all 293 Pilot-A runs:

- independent FlatExact baseline divergences: **0**;
- candidate-subset failures: **0**;
- observer-equivalence failures: **0**;
- observer-witness-minimality failures: **0**;
- effect-model ratio mismatches: **0**;
- expected-release byte mismatches: **0**.

A child-process failure or missing/identity-mismatched result is fail-closed and would stop the campaign.

A separate post-run integrity audit rehashed all 293 raw child artifacts against the aggregate ledger and found 0 missing trials, 0 result-hash/identity mismatches, 0 correctness-gate failures and 0 effect-model mismatches. The five frozen paired-physical result SHA-256 values were also resolved to existing artifacts. This is a post-run evidence-integrity audit, not a retroactive preregistration claim.

## Sensitivity-sweep result

| Family | Sweep cases | SAR min | SAR median | SAR max | Interpretation |
| --- | ---: | ---: | ---: | ---: | --- |
| BranchBench deep refinement | 20 | 1.166x | **1.667x** | 1.941x | Benefit increases with retained depth and shadow coverage. |
| Low-shadow negative control | 12 | 1.0005x | **1.0168x** | 1.0888x | Correctly shows little/no material benefit in very-low-divergence regimes. |
| Published wide-mutation sensitivity | 126 | 1.040x | **1.248x** | 51.0x | Wide branch count/shadow/tombstone sensitivity; high tombstone results must be separated from overwrite-only results. |

Threshold view:

- deep family: 20/20 >= 1.10x, 19/20 >= 1.25x, 16/20 >= 1.50x;
- negative-control family: 0/12 >= 1.10x;
- wide family: 94/126 >= 1.10x, 63/126 >= 1.25x, 47/126 >= 1.50x;
- overwrite-only wide subset: median 1.231x, maximum 1.980x; the closed-form overwrite bound approaches but does not exceed 2x.

The 51x wide maximum is a full-tombstone / 50-branch semantic bound (`B+1`), not a representative overwrite result and must not be used as the headline effect size.

## Repeated sentinel results

Each selected sentinel was run across five preregistered seeds and three independent processes per seed (15 runs per case). Effect ratios are deterministic for the controlled synthetic configuration; process repetition measures execution-time stability and rechecks all correctness gates independently.

| Sentinel | Median SAR | Median verified projection time |
| --- | ---: | ---: |
| deep d8 / 25% overwrite | 1.667x | 124 ms |
| deep d16 / 50% overwrite | 1.889x | 357 ms |
| negative B8 / 0.1% overwrite | **1.00087x** | 158 ms |
| negative B8 / 10% overwrite | 1.0888x | 194 ms |
| wide B8 / 20% overwrite | 1.1777x | 195 ms |
| wide B8 / 50% overwrite | 1.4444x | 232 ms |
| wide B8 / 50% full-tombstone shadow | 1.800x | 225 ms |
| wide B16 / 50% shadow / 25% tombstone | 1.533x | 526 ms |
| wide B32 / 75% shadow / 25% tombstone | 1.889x | 1,016 ms |

## Frozen paired-physical tier

The five v3 physical cases were run against paired copies of the same ChronicleDB image: current FlatExact GC versus descendant-first shadow-aware GC, followed by compaction and restart observer comparison.

| Physical case | Logical SAR | Released logical payload | Allocated-byte reduction | Allocated / logical | Restart observers |
| --- | ---: | ---: | ---: | ---: | --- |
| B8 / 10% overwrite negative control | 1.089x | 3.19 MiB | 3.25 MiB | 1.018x | equal |
| B8 / 25% overwrite | 1.222x | 8.00 MiB | 8.14 MiB | 1.017x | equal |
| B8 / 50% overwrite | 1.444x | 16.0 MiB | 16.29 MiB | 1.018x | equal |
| B8 / 100% overwrite | 1.889x | 32.0 MiB | 32.56 MiB | 1.017x | equal |
| B8 / 100% tombstone | 9.000x | 32.0 MiB | 32.56 MiB | 1.017x | equal |

All five physical measurements reported exact allocation accounting and effect-model equality. One first-attempt full-overwrite parent summary artifact was absent despite a PASS log, so that case was **not counted**; the same preselected case was rerun independently and produced a complete result artifact. No workload parameter was changed.

Frozen physical result-file SHA-256 values, in table order:

- `7f9f18fb1287f6426e00d40ba9b5cb7d77ffd1ca7d8f97d1f0a34ea534e2cfb5`;
- `68f92872c4ac4a6cd660e4829c058619e8e35a71f764cef07f4f5336ebbddd94`;
- `5581606b3432e646159dbe819d0572fc5a9c9f86eb7b27a30f25b109ce07b430`;
- `bc5cf12ce5a3570b01fc3dbfeb612d4342197b2852f5b7a3a56f265aeefbe09d`;
- `ca45d8af26d7df882b40c53d058085eabab7c0b88824d514996cc86162905005`.

## Pilot-A decision

The A1 hypothesis **survives Pilot-A**.

The evidence supports a bounded claim:

> Shadow-aware cross-history MVCC projection can remove retained ancestor predecessors that a strong per-history FlatExact baseline must conservatively preserve, while maintaining all retained observer results. The benefit is strongly regime-dependent: negligible under very low shadow, material under moderate/deep overwrite workloads, and larger when tombstones stop inherited fallback without carrying replacement payload.

Pilot-A does **not** justify opening or selectively reporting only favorable regimes. Low-shadow negative controls remain mandatory.

## Before Holdout-A

1. Apply/rebase the A1 commit chain onto the current GitHub `main` and rerun the complete regression gate there.
2. Freeze the exact code commit/toolchain/machine block used for Holdout-A.
3. Do not change candidate mechanics, publication axes, selected Holdout cases or interpretation rules after this Pilot-A result.
4. Open only the presealed Holdout-A partition. Holdout-B remains unopened unless A is invalidated by a preregistered correctness/infrastructure failure.
