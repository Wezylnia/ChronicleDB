# BranchCheck Paper Execution Plan

Status: frozen on 2026-08-14 at ChronicleDB commit `f9ca2c33e451fdaaa5d0aa04a7e44eb0cdbaf586`.

This document freezes the paper contract before new evidence is collected. The current implementation is treated as the BranchCheck v0.4 research prototype. Subsequent work must produce evidence, improve reproducibility, or write the paper; framework expansion requires an explicit link to an RQ, table, figure, baseline, or validity threat below.

## Working direction

**BranchCheck: Beyond Snapshot Equality — Testing the Future Semantics of Database Branches**

Central hypothesis:

> A database branch may be observationally correct at creation time while already containing incorrect latent state that causes divergence under a later supported operation.

The defensible contribution is narrower than a claim about generic fuzzers:

> Branch contracts expose semantic obligations that can guide the construction of high-value continuation traces and diagnose latent branch-state failures more efficiently and systematically than relation-agnostic exploration.

Generic testing may detect a terminal failure once the correct continuation is supplied. BranchCheck is evaluated primarily on witness construction and semantic diagnosis, not exclusive detection.

## Frozen research questions

1. **RQ1 — Failure characterization:** What latent state must database branches preserve for future operations to remain semantically valid?
2. **RQ2 — Expressiveness:** Can heterogeneous database branch semantics be represented as capability-aware executable obligations?
3. **RQ3 — Witness generation:** Under equal budgets, does obligation-guided exploration discover violating continuation traces more effectively than relation-agnostic exploration?
4. **RQ4 — Real-system effectiveness:** Can BranchCheck reproduce historical branch failures and expose failures in current real systems?
5. **RQ5 — Generality:** Do the same obligation families apply across substantially different branch implementations and architectures?

## Frozen non-claims

- BranchCheck does not invent stateful DBMS fuzzing or differential testing.
- BranchCheck does not define a universal database-branch semantic.
- BranchCheck does not claim generic fuzzers cannot detect branch failures.
- BranchCheck does not claim every branch bug requires a BranchCheck-specific oracle.
- BranchCheck does not claim every generated continuation is complete.
- The current Dolt regression candidate is not called novel without independent upstream confirmation.
- Small candidate-space results are not presented as universal speedups.
- ChronicleDB is a controlled laboratory, not the primary external novelty evidence.

## Required evidence gates

Before submission, freeze a machine-readable provenance table for every reported experiment. Each row must include backend and exact version/commit, capability grammar, seed set, operation budget, command line, timeout, exit code, result JSON, human-readable log, and environment-manifest identity.

The target evaluation is:

- at least three independent systems;
- at least three executable latent-state obligation families;
- larger capability-derived candidate spaces that do not encode issue IDs;
- explicit B0–B5 generic baselines and BranchCheck (BC) guidance;
- an unseeded campaign with all outcomes, including false positives and infrastructure failures;
- a controlled Dolt causal A/B experiment and upstream reproducibility decision;
- a controlled ChronicleDB mutation study;
- reduced witnesses with semantic failure signatures;
- final tables and figures generated from immutable artifacts.

## Obligation taxonomy to freeze

The working families are:

- Continuation-State Preservation
- Temporal Identity Stability
- Boundary Consistency
- Observer Closure
- Dependency Closure
- Mutation Viability
- Lifecycle Closure
- Recovery Closure

Each family must specify its capability, contract assumption, state, reference construction, legal witness continuation, observation points, expected relation, historical cases, executable backend, generic-baseline detectability, and the reason semantic guidance helps.

## Baseline matrix

| Baseline | Definition |
|---|---|
| B0 | Creation data equality |
| B1 | Creation visible-state equality |
| B2 | Generic stateful differential testing |
| B3 | Generic recovery testing |
| B4 | Generic branch-operation grammar |
| B5 | Generic alternate-observer testing |
| BC | BranchCheck obligation-guided testing |

For every experiment report separately whether a supplied failing trace is detected and whether the baseline constructs the relevant witness under the same budget.

## Execution order

1. Freeze this contract and environment manifest.
2. Reproduce the v0.4 baseline locally and record all unavailable backends explicitly.
3. Stabilize independent scripts, seeds, budgets, JSON artifacts, logs, and exit codes.
4. Re-audit and deduplicate the historical corpus.
5. Formalize the taxonomy and expand capability-derived grammars.
6. Run larger MatrixOne and Dolt campaigns plus a third independent backend/family.
7. Run the unseeded campaign, Dolt race/causal study, and upstream decision.
8. Run the ChronicleDB controlled mutation study and trace reduction.
9. Freeze results, generate tables/figures, then write Evaluation and Methodology before Introduction and Abstract.

## Local execution status (2026-08-15)

| Step | State | Evidence |
|---|---|---|
| A–B. Patch, build, and existing tests | Complete | frozen baseline: 575 tests; 2026-08-15 verification after 5 evidence/audit/fairness/preregistration tests: 580/580 pass, 0 build warnings/errors |
| C. Environment freeze | Complete | `artifacts/environment/environment.json` |
| D. Local v0.4 replay | Complete for available backends | synthetic + historical JSON; external prerequisites recorded unavailable |
| E. Reproducibility artifacts | Complete for local runs | command logs, JSON outputs, exit codes under `artifacts/baseline/` |
| F. Historical corpus audit | Complete for seven primary cases | root-cause deduplication + 2026-08-15 upstream state/fix re-audit in `BRANCHCHECK_HISTORICAL_CORPUS_AUDIT.md` |
| G. Obligation taxonomy | Initial freeze | `BRANCHCHECK_OBLIGATION_TAXONOMY.md` |
| H. Capability-derived grammar | Implemented and unit-tested | `CapabilityCandidateGrammar` and 5 grammar tests |
| E. Seed/budget protocol | Implemented locally | `CapabilityBudgetCampaign`; 4-profile calibration with 8 frozen seeds |
| L. Unseeded campaign protocol | Implemented as local pilot | `BRANCHCHECK_UNSEEDED_LOCAL_CAMPAIGN.md`; 128 predeclared-seed runs, explicitly not external evidence |
| N. False-positive / trigger-oracle analysis | Initial freeze | `BRANCHCHECK_FALSE_POSITIVE_ANALYSIS.md` |
| Q. Evaluation tables | Local scaffold frozen and external freeze linked | `BRANCHCHECK_EVALUATION_TABLES.md`; local and imported-external artifacts remain explicitly separated |
| Reproducibility gate | Local gate complete; frozen external CI evidence imported and validated; fresh local external rerun pending | `BRANCHCHECK_REPRODUCIBILITY_CHECKLIST.md`; `BRANCHCHECK_EXTERNAL_EVIDENCE_FREEZE.md` |
| O. ChronicleDB controlled mutation study | Implemented locally | `BRANCHCHECK_CHRONICLEDB_MUTATION_STUDY.md`; 5 mutation cases pass |
| O. Trace reduction | Implemented locally | `BranchScenarioReducer`; semantic-signature reduction tests pass |
| I. MatrixOne external identity / fair budget | Identity evidence preserved; old budget fairness rejected; v2 preregistered | immutable CI artifact validates B0/B2/B4 Pass + BC temporal Fail. Re-audit found exact-recipe leakage in the old 5-recipe guide, so 20% vs 100% is legacy controlled evidence only. Current code freezes a 10-recipe / 3-risk-class v2 grammar (`1FA61958...`) pending external execution. |
| J. Dolt external fair budget | Existing external evidence preserved | Dolt 2.2.3 frozen artifact validates 75% vs 100% budget-1; larger candidate space and final-main local rerun still pending |
| K. Third external system/family | Partial gate met | SlateDB buggy/fixed observer evidence supplies an independent observer/dependency family, but its guided 3-candidate budget is not counted as fair-search evidence |
| L. External unseeded campaign | Pending | local 128-run protocol pilot exists; no live external unseeded campaign yet |
| M. Dolt race/causal study | Existing external evidence preserved | release repetition + pinned current-main 20x/20x causal A/B validate the current regression candidate; larger local timing/load sweep remains optional strengthening |
| N. Upstream confirmation | Pending | maintainer-ready issue draft exists; no matching public issue found in the latest targeted search |

A consolidated Done/Partial/Open A–O matrix is frozen in `BRANCHCHECK_PLAN_COMPLETION_20260815.md`. It is the current execution checklist for local continuation.

## 2026-08-15 external evidence preservation update

Five GitHub Actions artifacts from the final BranchCheck research branch are now stored under `artifacts/external-frozen/raw/` with a manifest and a fail-closed `external-evidence` validator. The bundle covers MatrixOne, SlateDB, and Dolt and validates archive digests plus the exact paper-facing semantic polarity. This closes the evidence-preservation gap but does not replace a fresh final-`main` local rerun. See `BRANCHCHECK_EXTERNAL_EVIDENCE_FREEZE.md`.

The remaining high-value work is now narrow: execute the preregistered MatrixOne v2 fair budget, enlarge Dolt fairly, run an external unseeded campaign across at least three latent-state families, and obtain upstream/independent confirmation of the Dolt current regression candidate. Framework expansion without one of those goals remains frozen.

## Stop conditions

Stop adding engineering if guided search matches generic search on larger fair spaces, the advantage depends on issue-specific encoding, new families require backend-specific assertions, false positives remain high, fewer than three substantially different systems are supported, or external findings cannot be independently reproduced. In those cases write the paper around the empirical characterization and limitations.
