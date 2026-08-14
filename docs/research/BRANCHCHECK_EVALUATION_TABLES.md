# BranchCheck Evaluation Tables (local freeze)

These tables are a paper-ready local scaffold. Rows marked calibration, laboratory, descriptive, or unavailable must not be promoted to external discovery evidence.

## Table 1 — Systems and capability evidence

| System | Architecture evidence | Branch primitive | Historical boundary | Writable continuation | Observers | Restart/lifecycle | Current evidence |
|---|---|---|---|---|---|---|---|
| ChronicleDB | persistent MVCC history tree | real historical branch | yes | yes | snapshot + historical view | restart + delete | live local integration; controlled mutations |
| MatrixOne | SQL data branch / snapshot | data branch | yes | yes | SQL observers | external live unavailable | curated history + adapter code |
| YugabyteDB | cloned database | clone | implicit | yes | ordinary DB observers | restart | curated history only |
| Dolt | clone/provider history | `DOLT_CLONE` | history import | yes | SQL provider | drop/restart | curated history; live unavailable |
| Neon | timeline branch | old-LSN branch | yes | recovery continuation | compute | restart | curated history only |
| SlateDB | zero-copy clone | manifest-backed clone | parent boundary | read/observer | `Db` + `DbReader` | external probe | curated history; live unavailable |

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

| Campaign | Local status | Missing prerequisite | Publication status |
|---|---|---|---|
| MatrixOne identity / budget | unavailable | Docker + MySQL client + pinned image digest | pending |
| SlateDB buggy/fixed observer | unavailable | Rust/Cargo + pinned crate/commit | pending |
| Dolt releases/current-main | unavailable | Dolt binaries + Go toolchain | pending |
| ChronicleDB integration/mutations | reproducible | none | laboratory only |
