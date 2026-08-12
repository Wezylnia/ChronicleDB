# A1 Shadow-Aware Retention — Holdout Plan

Status date: 2026-08-12

This document freezes the **pre-execution Holdout planning layer** for the v3 A1 publication plan. It does not record Holdout evidence: **Holdout-A and Holdout-B remain unopened**.

## Frozen parent plan

- Candidate: `A1-shadow-retention`.
- Publication plan SHA-256: `5906da5feaed5ed85c3926ee38232a6aeb708a8aaf8d02f6e942bcb6a3e24302`.
- Pilot-A has completed separately; its results must not be used to retune the seven frozen Holdout cases.

## Sealed A/B execution identities

`--write-holdout-plans <sealed-publication-plan-directory> <output-directory>` derives and immutably writes both Holdout partitions without executing a child process.

- Holdout execution-plan SHA-256: `c955c5808fddafcdf4bada4af0f36a7e6c8b4631ed40590a44b7d4654817aa61`.
- Holdout-A: **210 runs** = 7 cases × 10 seeds × 3 process repetitions.
- Holdout-B: **210 runs** = 7 cases × 10 distinct seeds × 3 process repetitions.
- Total presealed identities: **420**.
- A and B use disjoint seed partitions and independently deterministic trial order derived from the frozen publication-plan hash and full run identity.

The command was executed twice into independent directories and the execution/analysis JSON plus SHA sidecars were byte-for-byte identical. This is a planning/determinism check only; no Holdout seed was executed.

## Frozen analysis procedure

- Holdout analysis-plan SHA-256: `7eb6376a481694be718feca93351ef1690fe5ae5fa56403d0ccd1477eaa3fb4e`.
- Initial partition: **Holdout-A only**.
- Runs per case: **30**.
- Reported quantiles per case: **P05 / P50 / P95**.
- Quantile rule: linear interpolation at `index=(n-1)*p`.
- Primary recorded metrics: retained-payload SAR, released logical payload bytes, verified-projection time, and thread allocation.
- Required result gates: result hash/identity, independent FlatExact equality, candidate subset, observer equivalence, witness minimality, and exact controlled-family effect-model agreement.

All seven case summaries must be reported separately. The low-shadow negative control `holdout-neg-b08-s001` is mandatory even if its effect is negligible. Overwrite-only and tombstone-containing cases remain separate; the maximum tombstone ratio cannot be used as a universal headline effect.

No successful preregistered run may be excluded after its effect size or runtime is observed. Any correctness, identity, result-hash, expected-release, or effect-model mismatch invalidates the partition instead of becoming an exclusion.

## Gate before opening Holdout-A

1. Apply the A1 chain to the exact current GitHub `main` content and rerun the complete build/test gate on that integrated source.
2. Freeze the exact integrated source revision, .NET SDK/toolchain and declared machine block in a final Holdout registration artifact.
3. Verify the registration refers to the publication, execution and analysis SHA-256 values above.
4. Only then execute the presealed Holdout-A partition.
5. Keep Holdout-B unopened unless Holdout-A is invalidated by the already declared correctness/infrastructure rule; weak effect size is not a reason to open B.

The planning layer intentionally contains no automatic paper-selection threshold. Holdout tests the frozen bounded claim and reports the complete conditional effect surface rather than optimizing for a single favorable ratio.
