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

## Imported frozen external evidence (2026-08-15)

The rows above remain the truthful record of what the original Windows local baseline could execute. They are **not** overwritten. Separately, immutable GitHub Actions artifacts produced by the final BranchCheck research branch were imported under `artifacts/external-frozen/raw/` and validated by `ExternalEvidenceBundleValidator`.

Run:

```text
dotnet run -c Release --no-build --project tools/ChronicleDB.BranchCheck -- external-evidence artifacts/external-frozen/manifest.json
```

| Evidence | GitHub artifact | Source head | Integrity | Paper-facing result |
|---|---:|---|---|---|
| MatrixOne continuation + identity + legacy budget | `9224759215` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | SHA-256 + required entries + semantic polarity PASS | continuation is generic-detectable negative control; identity is B0/B2/B4 Pass + BC temporal Fail; legacy budget-1 20%/100% is preserved but excluded from fair RQ3 evidence because guidance selected the known failing recipe directly |
| SlateDB buggy/fixed pair | `9224859095` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | PASS | buggy observer fails BC and B5; fixed observer passes; 3-candidate budget excluded from fair-search claims |
| Dolt 2.2.3 fair budget | `9224706547` | `253a80652669f1e91fee2c6256ca6d491fe9aca2` | PASS; explicit artifact-selection caveat in manifest | 3/4 recipes violate continuation; B4 passes; budget-1 generic/guided 75%/100% |
| Dolt release repetition | `9224757113` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | PASS | 2.2.3 10/10 Pass; frozen 2.3.0 sample 7/10 Pass + 3/10 `context canceled` |
| Dolt current-main causal A/B | `9224930424` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | PASS | unpatched 12/20 Pass + 8/20 `context canceled`; causal control 20/20 Pass |

`artifacts/external-frozen/manifest.json` is authoritative for IDs, digests, backend identities, required archive entries, and the one Dolt 2.2.3 artifact-selection exception. `artifacts/external-frozen/validation.json` is the current machine-readable validation result.

These imported artifacts are external evidence, but they are not described as fresh executions on the current local machine. Fresh final-`main` WSL2/Docker reproduction remains a reproducibility task before final artifact submission.

## Current paper-gate verification (2026-08-15)

The frozen 575-test baseline above is intentionally retained unchanged. After adding the external-evidence validator, upstream-status audit regression, and MatrixOne v2 fairness regression, the current working tree was independently rebuilt with the offline .NET 10.0.301 toolchain:

- full Release solution build: **0 warnings, 0 errors**;
- full solution tests: **580 passed, 0 failed, 0 skipped**;
- focused BranchCheck tests: **44/44 passed**;
- local capability-budget calibration: 20 identity, 10 allocator, 15 observer, and 12 recovery candidates; all four calibration profiles retain a guided advantage (calibration only, not external RQ3 evidence);
- unseeded-local protocol: **128 runs** across four families, retained as protocol calibration rather than external discovery evidence;
- frozen external-evidence validation: **5/5 artifacts passed** integrity/structure/semantic checks;
- MatrixOne legacy budget is explicitly marked target-seeded; MatrixOne v2 is frozen before external execution in `artifacts/external-frozen/matrixone-v2-preregistration.json`.
