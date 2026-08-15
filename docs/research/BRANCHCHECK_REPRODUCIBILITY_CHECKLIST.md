# BranchCheck Reproducibility Checklist

This checklist is the gate for promoting a result into paper evidence. It now distinguishes **fresh local execution** from **immutable imported external evidence** produced by GitHub Actions.

## Per-experiment identity

| Requirement | Local baseline | Imported external freeze | Fresh external rerun |
|---|---|---|---|
| exact ChronicleDB commit | recorded | research head + import-main SHA recorded | required |
| backend version/commit/image digest | N/A/unavailable rows explicit | recorded in manifest/artifact | required |
| candidate grammar fingerprint | recorded where applicable | preserved by frozen research head; add to future expanded campaigns | required |
| fixed seed set | 8/32 local ledgers | preserved where original campaign used seeds/orderings | required |
| operation/trace budget | recorded | frozen JSON | required |
| exact command line | provenance + runner mode | original workflow + runner mode | required |
| timeout and exit code | recorded | workflow provenance | required |
| machine-readable JSON | present | preserved inside immutable ZIPs | required |
| human-readable log | present | preserved where produced by original workflow | required |
| environment identity | local environment manifest | GitHub artifact ID, workflow run, source head, backend identity | required |
| artifact integrity | local files | SHA-256 + required-entry + semantic-polarity gate | required |

## Campaign integrity

- [x] Full restore/build/test baseline exists for the frozen local runner state.
- [x] Architecture suite and BranchCheck suite pass in the frozen baseline.
- [x] 2026-08-15 offline Linux verification after evidence/audit/fairness additions: full solution 580/580 tests pass, Release build 0 warnings/errors.
- [x] Historical corpus and synthetic baseline artifacts parse as JSON.
- [x] External prerequisites were recorded as unavailable rather than silently omitted on the Windows baseline.
- [x] Local budget and unseeded pilots declare `ExternalEvidence: false`.
- [x] False-positive and trigger/oracle distinctions are documented.
- [x] MatrixOne frozen external artifact imported with image digest and semantic polarity validation; its legacy 5-recipe budget is explicitly marked target-seeded and excluded from fair RQ3 evidence.
- [x] SlateDB buggy/fixed frozen artifact imported with both Cargo.lock files and paired semantic validation.
- [x] Dolt 2.2.3 fair-budget frozen artifact imported and validated.
- [x] Dolt release repetition and pinned current-main causal A/B artifacts imported and validated.
- [x] Three independent external systems are represented in immutable validated artifacts: MatrixOne, SlateDB, and Dolt.
- [x] Three distinct external latent-state families are represented: temporal identity, observer/dependency closure, and continuation authority.
- [ ] Fresh final-`main` local/WSL2 rerun of MatrixOne, SlateDB, and Dolt with pinned identities.
- [ ] Execute preregistered MatrixOne v2 (10 recipes; 3 semantic identity-risk recipes; fingerprint `1FA61958...`) and enlarge Dolt beyond the current 4-candidate fair campaign.
- [ ] External unseeded campaign across at least three latent-state families.
- [ ] A fair-search experiment for a third non-identity/non-allocator family, preferably dependency, lifecycle, or recovery.
- [ ] Upstream/independent confirmation or fix status captured for the Dolt dynamic-clone regression candidate.

## Imported evidence gate

Run:

```text
dotnet run -c Release --no-build --project tools/ChronicleDB.BranchCheck -- external-evidence artifacts/external-frozen/manifest.json
```

A result in the imported bundle may be used only if:

1. the archive SHA-256 matches `manifest.json`;
2. every required archive entry exists and is non-empty when parsed as evidence;
3. the paper-facing semantic polarity matches the frozen expectation;
4. any artifact-selection exception is explicit in the manifest;
5. the paper labels the result as imported frozen CI evidence, not as a fresh local rerun.

## Promotion rule

A result may enter a paper table only when every required identity field is present, the raw evidence is immutable, the interpretation/validity note is explicit, and the result passes either the fresh-run provenance gate or the imported-evidence integrity gate. Local calibration and ChronicleDB controlled mutations remain separate laboratory tables. Imported external artifacts remain valid historical evidence, but the final artifact package should still provide scripts for fresh reproduction on WSL2/Docker/Linux.
