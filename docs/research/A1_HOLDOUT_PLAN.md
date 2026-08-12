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

## Executable source/binary registration gate

The holdout tooling now includes an executable registration boundary rather than relying on this Markdown file as authority.

```text
--prepare-holdout <sealed-plan-dir> <output-dir> <machine-block-id> <expected-main-base-commit>
--run-holdout-a <prepared-holdout-dir> <machine-block-id>
--run-holdout-b <prepared-holdout-dir> <machine-block-id>
```

`--prepare-holdout` runs **before any Holdout child process**. It refuses to seal a campaign unless:

- the Git working tree is completely clean, including untracked files;
- the declared GitHub-main base is an ancestor of the exact source commit;
- the output directory is outside the source working tree;
- publication, A/B execution and analysis plans validate against one another;
- source commit and source tree IDs are recorded;
- the declared machine-block ID is recorded;
- .NET framework, OS, process architecture and OS architecture are recorded;
- every executable ChronicleDB `*.dll`, pilot `.deps.json` and `.runtimeconfig.json` in the pilot output directory is recorded by name, byte length and SHA-256;
- Holdout-B is already present in the sealed execution plan before A can execute.

The resulting registration is immutable and binds the publication-plan, execution-plan and analysis-plan SHA-256 identities to the exact source/binary/environment identity. `--run-holdout-a` recomputes these identities and refuses execution if any registered source, binary, runtime or machine-block field differs.

As of this freeze, the GitHub source-of-truth main commit is `5fa3d3835c42e929cef14ab90288e04b9e5c113b`. The current research checkout is based on the pre-rewrite local ancestry, so a real prepare attempt declaring that main commit correctly fails before writing a registration. **This is intentional; production Holdout-A remains unopened until the A1 chain is composed onto the real current main checkout and the full regression gate passes there.**

## Executable A/B fallback rule

`--run-holdout-a` is resume-safe only for already complete, identity-verified child result artifacts. It writes a final aggregate only from the sealed A run set and the frozen linear-interpolation P05/P50/P95 analysis method.

Holdout-B is mechanically inaccessible unless an immutable Holdout-A invalidation artifact exists. The invalidation category can only be `CorrectnessFailure` or `InfrastructureFailure` and is bound to:

- the sealed registration SHA-256;
- the sealed execution-plan SHA-256;
- the exact failed A run ID;
- an immutable failure-evidence SHA-256.

A successfully completed Holdout-A does **not** create such an invalidation. Therefore a weak SAR, unfavorable runtime or otherwise disappointing but correct A result cannot open B.

Two executor-only smoke campaigns were run with separate smoke candidate IDs and non-publication seeds; **none of the frozen 1101–1110 / 2101–2110 Holdout seeds were executed**:

1. source/binary registration + A execution smoke: A=2, B=2 were presealed; B was rejected before A; A completed 2/2; B remained rejected after successful A;
2. fallback smoke: a deliberately invalid synthetic case produced an A infrastructure invalidation and immutable failure evidence; only then did B pass the fallback gate and start its own deliberately invalid child.

These are tooling/protocol checks, not Holdout evidence. Current publication status remains **Holdout-A 0/210; Holdout-B 0/210**.
## Current local validation before main composition

The source state containing the complete holdout planning/registration/executor tooling passes the Release validation gate:

- solution build: **0 warnings / 0 errors**;
- Architecture: **8 / 8 PASS**;
- Unit: **214 / 214 PASS**;
- Persistence: **180 / 180 PASS**;
- Correctness: **25 / 25 PASS**;
- Recovery: **55 / 55 PASS**;
- total xUnit: **482 / 482 PASS**.

This validates the local A1 source state only. It does not replace the required regression after composing the A1 chain onto the actual GitHub `main` checkout.

