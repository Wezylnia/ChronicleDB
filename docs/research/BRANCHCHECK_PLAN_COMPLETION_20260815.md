# BranchCheck Plan Completion Snapshot — 2026-08-15

Baseline: ChronicleDB `main@0142a12fab77eb8cca87b201eaf3953ef2c80592`.

This snapshot separates **prototype completion** from **paper-evidence completion**. A feature being implemented does not mean the corresponding research question is closed.

## A–O execution matrix

| Step | Engineering state | Scientific state | Evidence / remaining work |
|---|---|---|---|
| A. Apply/freeze BranchCheck on clean `main` | **Done** | N/A | Current archive, local git, `origin/main`, and GitHub `main` all resolve to `0142a12...`. |
| B. Build and existing tests | **Done** | N/A | Offline .NET 10.0.301 restore/build works. Current verification after adding the evidence/audit/fairness regressions is 580/580 tests passing with 0 build warnings/errors; the earlier frozen baseline recorded 575 tests before these five paper-gate tests were added. |
| C. Environment + provenance freeze | **Done** | **Done for current local baseline** | `artifacts/environment/environment.json`, `artifacts/baseline/PROVENANCE.md`, external artifact manifest/digests. |
| D. Reproduce v0.4 baseline | **Done locally where dependencies exist** | **Partial externally** | Local synthetic/historical/ChronicleDB evidence reruns; frozen MatrixOne/SlateDB/Dolt CI evidence is imported but is not a fresh final-main Windows/WSL rerun. |
| E. Reproducibility scripts/seeds/budgets | **Done for current campaigns** | **Done for reported small-space experiments** | JSON outputs, fixed seed/budget protocol, fail-closed external evidence validator. Larger external campaigns need their own preregistered freeze before execution. |
| F. Historical corpus re-audit | **Done for seven primary cases** | **Done for current paper corpus, appendix expansion optional** | Root-cause deduplication plus 2026-08-15 public upstream status/fix re-audit. Remaining task is only preservation of excluded/ambiguous appendix cases. |
| G. Obligation taxonomy | **Done** | **Frozen** | Eight obligation families in `BRANCHCHECK_OBLIGATION_TAXONOMY.md`; do not expand without new external evidence. |
| H. Capability-derived candidate grammar | **Done** | **Validated as protocol, not broad effectiveness claim** | Unit-tested grammar and local calibration. |
| I. Larger MatrixOne experiment | **Identity adapter done; legacy 5-candidate search preserved; v2 10-candidate grammar implemented** | **Open / rerun required** | Temporal-identity separation remains strong. Fairness re-audit rejected the old 20%→100% search curve because the guide selected the exact failing recipe. Current v2 freezes 10 recipes, 3 semantic source-identity-risk recipes, analytic class-fair rates, fingerprint `1FA61958...`; **external execution is still missing**. |
| J. Larger Dolt experiment | **Adapter + 4-candidate fair campaign done** | **Partial** | Frozen Dolt 2.2.3 result: 75% vs 100% at budget 1. **Missing:** larger fair candidate space; current 2.3 results are contaminated by the separate clone-lifetime race and are not used as the fair headline. |
| K. Third independent backend/family | **SlateDB adapter + paired regression done** | **Partial** | Observer/dependency relation has real buggy/fixed evidence. Its 3-candidate guided budget is intentionally invalid as fair-search evidence because the target is effectively hard-coded. **Missing:** fair third-family search experiment, preferably dependency/lifecycle/recovery. |
| L. Unseeded campaign | **Protocol pilot done** | **Open / mandatory** | 128-run local protocol calibration exists. **Missing:** external campaign on current systems, grammar frozen before observation, across >=3 latent-state families. |
| M. Dolt race/causal study | **Done at v0.4 strength** | **Strong but not upstream-confirmed** | Release repetitions, current-main source reproduction, ordinary-read-after-failure control, and 20x/20x causal A/B are frozen. Optional timing/load sweep would strengthen mechanism, but upstream confirmation has higher value. |
| N. Upstream confirmation | **Issue draft done** | **Open / high value** | Targeted public search still found no exact `DOLT_CLONE` + AUTO_INCREMENT + `context canceled` issue; Dolt main is still the pinned failing source state in the frozen evidence. **Do not call the candidate confirmed until maintainer/independent confirmation exists.** |
| O. ChronicleDB controlled mutation + reduction | **Done** | **Done as controlled sensitivity evidence** | Five mutation cases plus semantic-signature reduction. This is laboratory evidence, not external relevance proof. |

## What is actually blocking submission-strength evaluation

Only four research gaps materially matter now:

1. **External unseeded discovery (L):** current-system campaign across at least three latent-state families with a grammar frozen before results are observed.
2. **Larger fair search spaces (I/J):** execute the already-frozen 10-recipe MatrixOne v2 grammar and enlarge Dolt beyond four operations without encoding known issue reproductions.
3. **Third-family fair-search evidence (K):** dependency, lifecycle, or recovery should survive a fair candidate-budget comparison on a real external backend.
4. **Independent/upstream confirmation (N):** the Dolt dynamic-clone race candidate should be reported/reproduced independently or fixed upstream.

Everything else is either complete, optional strengthening, or paper-writing work.

## Work that must NOT be expanded now

- No new generic fuzzer architecture.
- No new universal branch semantic model.
- No new ChronicleDB engine feature solely for the paper.
- No issue-specific candidate selector.
- No claim that generic baselines cannot detect supplied failing traces.
- No additional backend unless it closes K/L more effectively than the existing systems.

## Windows-local next execution order

1. Provision WSL2 + Docker only for the pinned external tools; keep Windows/.NET as the development host.
2. Fresh-rerun the frozen MatrixOne, SlateDB, and Dolt controls from final `main` to verify environment parity.
3. MatrixOne v2 is already frozen (`1FA61958...`); freeze the enlarged Dolt grammar **before** running it.
4. Execute MatrixOne v2 and the larger Dolt fair-budget experiments.
5. Freeze a multi-family external unseeded grammar and run it without post-hoc retargeting.
6. Use the first independent non-allocator/non-identity backend/family that produces a defensible fair-search result; do not force SlateDB if its grammar remains target-leaking.
7. Re-run/reduce any live failures and classify duplicates before issue filing.
8. File the Dolt candidate only after the minimal reproducer still survives the fresh environment; preserve maintainer response/fix as paper evidence.
9. Freeze tables/figures and write Evaluation + Methodology before Introduction/Abstract.
