# BranchCheck v0.4 Local Baseline

Captured on 2026-08-14. The original v0.4 campaign JSON was replayed at commit `f9ca2c33e451fdaaa5d0aa04a7e44eb0cdbaf586`; the final build/test logs include the capability-grammar commit `bee93789829256a439668cbfce4dec39f8769f8b`.

The machine and toolchain identity is in [`../environment/environment.json`](../environment/environment.json). Raw command output is kept beside this file. External-backend rows are recorded as unavailable rather than silently omitted.

| Experiment | Backend | Version / identity | Seed / budget | Expected result | Observed result | Status | Artifact |
|---|---|---|---|---|---|---|---|
| Full solution build | ChronicleDB | .NET 10.0.301; commit above | N/A | 0 errors/warnings | 0 errors, 0 warnings | PASS | `dotnet-build-release.log` |
| Full test suite | ChronicleDB + research harness | .NET 10.0.301 | N/A | all tests pass | 575 passed, 0 failed, 0 skipped | PASS | `dotnet-test-release.log` |
| Synthetic mutation campaign | BranchCheck synthetic | local v0.4 prototype | deterministic recipes | all declared relations classify correctly | 6 scenarios; 5 intended relation failures detected | PASS | `branchcheck-synthetic.json` |
| Historical issue campaign | BranchCheck corpus | 5 systems / 7 cases | curated transcript set | every case diagnosed by BC | 7/7 BC detected; 7/7 generic union also detected; 0 BC-only | PASS / limitation recorded | `branchcheck-historical.json` |
| Historical baseline matrix | BranchCheck corpus | same 5 systems / 7 cases | supplied traces only | B0–B5 vs BC status table | machine-readable comparison recorded; not witness-search evidence | PASS / descriptive | `historical-baseline-matrix.json` |
| Local capability-budget calibration | BranchCheck grammar | four capability profiles | 8 frozen seeds; all prefixes | uniform vs target-class-guided ordering | complete deterministic curves; calibration only | PASS / not external evidence | `local-capability-budget.json` |
| Unseeded local campaign pilot | BranchCheck grammar | four capability profiles | 32 frozen seeds; trace budget 8 | relation-agnostic local ordering | 128 runs; 114 known-failure and 14 no-failure classifications | PASS / protocol only | `unseeded-local.json` |
| Combined BranchCheck campaign | BranchCheck | local v0.4 prototype | deterministic recipes | synthetic + historical gates pass | process exit 0 | PASS | `branchcheck-all.json` |
| ChronicleDB integration | ChronicleDB adapter | local commit above | 1 test | roundtrip branch/history contract | 1/1 passed | PASS | `chronicledb-integration-test.log` |
| ChronicleDB controlled mutations | ChronicleDB adapter | `10ccc1af8b126195ffe69dd93f07ef8c01b98b9d` | deterministic mutation set | creation B0/B1 pass; semantic relation fails | 5/5 mutation cases and reducer checks pass | PASS / laboratory only | `chronicledb-controlled-mutations.json` |
| MatrixOne continuation | MatrixOne | unavailable | N/A | live continuation probe | SQL client `mysql` unavailable | UNAVAILABLE | `external/matrixone.log` |
| MatrixOne historical identity | MatrixOne | unavailable | N/A | live identity probe | SQL client `mysql` unavailable | UNAVAILABLE | `external/matrixone-identity.log` |
| MatrixOne trigger budget | MatrixOne | unavailable | N/A | fair-budget curve | SQL client `mysql` unavailable | UNAVAILABLE | `external/matrixone-budget.log` |
| SlateDB observer | SlateDB | unavailable | N/A | buggy/fixed observer probe | `BRANCHCHECK_SLATEDB_PROBE` absent; Rust/Cargo unavailable | UNAVAILABLE | `external/slatedb.log` |
| SlateDB trigger budget | SlateDB | unavailable | N/A | fair-budget curve | probe absent; Rust/Cargo unavailable | UNAVAILABLE | `external/slatedb-budget.log` |
| Dolt fair-budget | Dolt | unavailable | N/A | release campaign | Dolt executable unavailable | UNAVAILABLE | `external/dolt-budget.log` |
| Dolt clone smoke | Dolt | unavailable | N/A | continuation smoke | Dolt executable unavailable | UNAVAILABLE | `external/dolt-clone-smoke.log` |

## Reproduction commands

```text
dotnet restore --ignore-failed-sources
dotnet build -c Release /p:UseSharedCompilation=false
dotnet test -c Release --no-build --no-restore /p:UseSharedCompilation=false
dotnet run -c Release --no-build --project tools/ChronicleDB.BranchCheck -- all
dotnet test tests/Research/ChronicleDB.BranchCheck.Tests/ChronicleDB.BranchCheck.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~ChronicleDbAdapterIntegrationTests
```

The external rows were invoked independently with the corresponding `matrixone`, `matrixone-identity`, `matrixone-budget`, `slatedb`, `slatedb-budget`, `dolt-budget`, and `dolt-clone-smoke` modes. Each returned the expected harness-unavailable exit code `3` on this host.

## Interpretation boundary

This baseline is a reproducibility checkpoint, not new external evidence. The historical corpus remains curated, and the local synthetic campaign is a harness sanity check. The missing Docker/Rust/Go/Dolt prerequisites must be provisioned on a Linux runner before making live-backend claims.
