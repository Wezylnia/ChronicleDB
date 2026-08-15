# BranchCheck Evaluation Tables (local freeze)

These tables are a paper-ready local scaffold. Rows marked calibration, laboratory, descriptive, or unavailable must not be promoted to external discovery evidence.

## Table 1 — Systems and capability evidence

| System | Architecture evidence | Branch primitive | Historical boundary | Writable continuation | Observers | Restart/lifecycle | Current evidence |
|---|---|---|---|---|---|---|---|
| ChronicleDB | persistent MVCC history tree | real historical branch | yes | yes | snapshot + historical view | restart + delete | live local integration; controlled mutations |
| MatrixOne | SQL data branch / snapshot | data branch | yes | yes | SQL observers | live CI artifact | imported frozen continuation + historical-identity evidence; legacy 5-recipe budget retained but not counted as fair RQ3 evidence |
| YugabyteDB | cloned database | clone | implicit | yes | ordinary DB observers | restart | curated history only |
| Dolt | clone/provider history | `DOLT_CLONE` | history import | yes | SQL provider | long-lived provider + source-built causal control | imported 2.2.3 fair-budget + release repetition + current-main causal A/B evidence |
| Neon | timeline branch | old-LSN branch | yes | recovery continuation | compute | restart | curated history only |
| SlateDB | zero-copy clone | manifest-backed clone | parent boundary | read/observer | `Db` + `DbReader` | paired external probe | imported crate-0.14.1 buggy / fix-6a131a9e control |

## Table 2 — Historical supplied-trace baseline matrix

The authoritative machine-readable version is [`historical-baseline-matrix.json`](../../artifacts/baseline/historical-baseline-matrix.json). The supplied trace is already known; these rows answer diagnosis, not witness construction.

| Case | B0 | B1 | B2 | B3 | B4 | B5 | BC relation |
|---|---|---|---|---|---|---|---|
| MatrixOne #27092 | Pass | Inconclusive | Detected | N/A | N/A | N/A | continuation |
| MatrixOne #26120 | Pass | Inconclusive | N/A | N/A | Detected | N/A | temporal boundary |
| YugabyteDB #29335 | Pass | Inconclusive | Detected | N/A | N/A | N/A | continuation |
| YugabyteDB #32057 | Inconclusive | Inconclusive | N/A | Detected | N/A | N/A | recovery |
| Dolt #7106 | Inconclusive | Inconclusive | N/A | N/A | Detected | N/A | lifecycle |
| Neon #506 | Inconclusive | Inconclusive | N/A | Detected | N/A | N/A | temporal boundary + recovery |
| SlateDB #1902 | Pass | Inconclusive | Pass | N/A | N/A | Detected | observer dependency |

Summary: BC diagnoses 7/7; the union of generic B0–B5 also diagnoses 7/7; strict BC-only is 0/7.

## Table 3 — Local fair-budget calibration

The complete curves are in [`local-capability-budget.json`](../../artifacts/baseline/local-capability-budget.json). All runs use identical candidate sets and eight frozen seeds per profile.

| Profile | Candidate count | Target | Uniform budget-1 | Guided budget-1 | Evidence class |
|---|---:|---|---:|---:|---|
| historical identity | 20 | identity | 0.125 | 1.000 | harness calibration |
| allocator continuation | 10 | allocator | 0.250 | 1.000 | harness calibration |
| observer dependency | 15 | observer | 0.125 | 1.000 | harness calibration |
| recovery closure | 12 | recovery | 0.125 | 1.000 | harness calibration |

These rates are not external-system results because the local oracle is semantic-class membership.

## Table 4 — ChronicleDB controlled mutation study

The complete artifact is [`chronicledb-controlled-mutations.json`](../../artifacts/baseline/chronicledb-controlled-mutations.json).

| Mutation family | B0/B1 creation checks | BC relation | Result |
|---|---|---|---|
| fractured boundary | Pass / Pass | temporal boundary | 5/5 |
| stale continuation | Pass / Pass | continuation state | 5/5 |
| missing observer dependency | Pass / Pass | observer dependency | 5/5 |
| lost recovery lineage | Pass / Pass | recovery | 5/5 |
| non-idempotent lifecycle | Pass / Pass | lifecycle | 5/5 |

This is laboratory sensitivity evidence, not an external bug claim.

## Table 5 — External campaign readiness

| Campaign | Current evidence | Fresh local prerequisite | Publication status |
|---|---|---|---|
| MatrixOne identity / budget | immutable identity artifact validated; legacy 5-recipe budget reclassified as target-seeded | Docker/WSL2 + MySQL client + pinned image | identity result usable; preregistered 10-recipe v2 fair budget still pending |
| SlateDB buggy/fixed observer | immutable paired CI artifact; both Cargo.lock files retained | Rust/Cargo + pinned crate/commit | usable as paired regression evidence; budget not counted as fair search |
| Dolt 2.2.3 fair budget | immutable imported CI artifact | Dolt 2.2.3 in WSL2/Linux | usable as second-backend fair-search evidence; larger grammar pending |
| Dolt release/current-main race | release repetition + current-main causal A/B imported and validated | Dolt binaries + Go toolchain | usable as current regression candidate evidence; upstream confirmation pending |
| ChronicleDB integration/mutations | reproducible locally | none | laboratory only |

## Table 6 — Frozen external live evidence

The authoritative imported bundle is `artifacts/external-frozen/manifest.json`; `validation.json` is generated by the fail-closed `external-evidence` gate.

| Backend / case | Generic supplied-trace result | BC result | Search evidence | Interpretation |
|---|---|---|---|---|
| MatrixOne continuation | B1 + B2 detect | continuation Fail | not headline search evidence | negative control: BranchCheck is not oracle-exclusive |
| MatrixOne historical identity | B0 Pass; B2 Pass; B4 Pass | temporal-boundary Fail | legacy 5-recipe result was 20% vs 100% but is **not fair RQ3 evidence** because the old guide selected the exact failing recipe; 10-recipe semantic-class v2 is preregistered and pending | strongest branch-specific semantic example; search claim pending rerun |
| SlateDB 0.14.1 observer | B5 detects | observer/dependency Fail | 3-candidate guided budget rejected as unfair | paired regression validation only |
| SlateDB fix `6a131a9e` | B5 Pass | observer/dependency Pass | no violating candidate | fix polarity control |
| Dolt 2.2.3 history import | B2 detects once generated continuation is supplied; B4 passes operation outcomes | continuation Fail in 3/4 recipes | 4 recipes: generic 75%, guided 100% at budget 1 | second independent fair-search backend |

## Table 7 — Dolt dynamic-clone race / causal control

| Control | Runs | Pass | `context canceled` Fail | B4 clone grammar | Interpretation |
|---|---:|---:|---:|---|---|
| Dolt 2.2.3 release sample | 10 | 10 | 0 | Pass | older-version negative control |
| Dolt 2.3.0 frozen release sample | 10 | 7 | 3 | Pass | stochastic regression signature; not a universal probability estimate |
| pinned current main `c3b5ce3c...`, unpatched | 20 | 12 | 8 | Pass | source-built current regression reproduction |
| same source + one-line lifetime causal control | 20 | 20 | 0 | Pass | strong causal rescue; not a proposed production fix |

## Remaining table gaps before submission

1. execute the preregistered 10-recipe MatrixOne v2 fair search and enlarge Dolt beyond four recipes;
2. external unseeded campaign across at least three obligation families;
3. a fair-search result for dependency/lifecycle/recovery beyond allocator + identity;
4. upstream/independent confirmation status for the Dolt current regression candidate.
