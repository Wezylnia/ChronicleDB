# BranchCheck Frozen External Evidence Import

Status: validated on 2026-08-15 against ChronicleDB `main` commit `0142a12fab77eb8cca87b201eaf3953ef2c80592`.

This freeze imports immutable GitHub Actions artifacts produced by the BranchCheck research branch before it was collapsed into `main`. It does **not** claim that MatrixOne, SlateDB, or Dolt were rerun on the current local Windows host. The purpose is to keep already-produced external evidence auditable while local WSL2/Docker provisioning remains pending.

## Validation gate

Run:

```text
dotnet run -c Release --no-build --project tools/ChronicleDB.BranchCheck -- external-evidence artifacts/external-frozen/manifest.json
```

The gate fails closed on:

- archive SHA-256 mismatch;
- missing or empty required entries;
- an unknown evidence kind;
- a changed baseline/relation polarity;
- a changed fair-budget curve;
- a changed Dolt release-race signature;
- a changed current-main causal A/B signature.

The machine-readable output is `artifacts/external-frozen/validation.json`.

## Imported artifacts

| Key | GitHub artifact | Source head | External identity | Frozen semantic result |
|---|---:|---|---|---|
| `matrixone-live` | `9224759215` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | MatrixOne `8.0.30-MatrixOne-v4.1.4`; image `sha256:7161e9...` | continuation is a generic-detectable negative control; historical identity is B0/B2/B4 Pass + BC temporal Fail; the legacy 5-recipe 20% vs 100% budget result is preserved but reclassified as target-seeded, not fair RQ3 evidence |
| `slatedb-paired` | `9224859095` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | SlateDB crate `0.14.1` vs fix `6a131a9e` | buggy observer relation fails and B5 detects it; fixed version passes; the 3-candidate budget remains regression-only, not fair-search evidence |
| `dolt-223-budget` | `9224706547` | `253a80652669f1e91fee2c6256ca6d491fe9aca2` | Dolt `2.2.3` | `NoOp` passes; `FetchOnly`, `Pull`, and `FetchMerge` fail continuation while B4 passes; generic/guided budget-1 is 75%/100% |
| `dolt-release-repeat` | `9224757113` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | Dolt `2.2.3` vs `2.3.0` | 2.2.3 is 10/10 Pass; frozen 2.3.0 sample is 7/10 Pass and 3/10 `context canceled` Fail |
| `dolt-main-causal` | `9224930424` | `02bf57e79c8212e2136bc12ee85c54e656abf9d8` | Dolt source `c3b5ce3c...` | unpatched is 12/20 Pass + 8/20 `context canceled`; causal control is 20/20 Pass |

## Dolt 2.2.3 artifact selection caveat

The final research-head upload for the 2.2.3 budget contained a zero-length budget JSON and is rejected as paper evidence. The imported archive is the last non-empty frozen artifact (`9224706547`). Its source head differs from the final research head only by the temporary patch-export workflow lifecycle; `git diff` shows no change in the BranchCheck tool, BranchCheck tests, or Dolt paired-workflow semantics between the two commits.

This exception is explicit in `manifest.json`; the validator checks the selected archive digest and semantic shape instead of silently accepting the newest artifact.

## What this changes in the paper status

The local Windows baseline can now distinguish two facts that were previously conflated:

1. **local rerun availability:** MatrixOne/SlateDB/Dolt are still unavailable until WSL2/Docker/Rust/Go/Dolt are provisioned;
2. **existing external evidence:** three independent external systems already have immutable, validated CI artifacts.

Therefore the paper may use the imported artifacts as frozen external evidence with exact provenance. It must not describe them as fresh local reproductions.

## Remaining external gates

The imported bundle closes evidence preservation, not the whole evaluation. The following remain open:

- rerun the pinned external systems from the final local `main` under WSL2/Docker;
- execute the preregistered 10-candidate MatrixOne v2 grammar and expand Dolt beyond its current 4-candidate fair space;
- run a genuinely external unseeded campaign across at least three obligation families;
- obtain upstream or independent confirmation of the Dolt dynamic-clone regression candidate;
- preferably add a fair-search experiment for a non-allocator/non-identity family (dependency, lifecycle, or recovery).

## MatrixOne budget fairness correction

The imported five-recipe MatrixOne budget artifact remains immutable and valid as a record of what was executed, but it is **not** promoted to fair RQ3 search evidence. Re-audit of the v0.4 code found that the guided path selected `RecreateSourceSameName` directly. That violates the preregistered rule that guidance may use a semantic risk class but may not select the exact historically failing recipe.

Current `main` therefore replaces the future `matrixone-budget` campaign with a preregistered v2 grammar:

- 10 total source-history recipes;
- 3 source-identity-risk recipes (`RenameSourceRoundTrip`, `RecreateSourceSameName`, `RecreateSourceSameNameSchemaVariant`);
- exact recipe is never selected by the guidance rule;
- all orderings inside the risk class are treated uniformly through analytic detection probabilities;
- frozen candidate-set fingerprint: `1FA61958C7E97E5EC5BBC8F32D03D99BAAD902F5C360465A7594B8F053B52040`.

The v2 result is **pending external execution**. Until then Dolt 2.2.3 is the only external backend with a fair positive RQ3 budget result.
