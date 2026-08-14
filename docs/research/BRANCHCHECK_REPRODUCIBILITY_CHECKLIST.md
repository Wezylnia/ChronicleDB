# BranchCheck Reproducibility Checklist

This checklist is the gate for promoting a local result into paper evidence.

## Per-experiment identity

| Requirement | Local baseline | External campaign |
|---|---|---|
| exact ChronicleDB commit | recorded | required |
| backend version/commit/image digest | unavailable rows explicit | required |
| candidate grammar fingerprint | recorded in JSON where applicable | required |
| fixed seed set | 8/32 local ledgers | required |
| operation/trace budget | recorded | required |
| exact command line | provenance + runner mode | required |
| timeout and exit code | external exit code 3 recorded | required |
| machine-readable JSON | baseline and local pilots present | required |
| human-readable log | baseline logs present | required |
| environment manifest | `artifacts/environment/environment.json` | required |

## Campaign integrity

- [x] Full restore/build/test executed after the current local runner changes.
- [x] Architecture suite and BranchCheck suite pass.
- [x] Historical corpus and synthetic baseline artifacts parse as JSON.
- [x] External prerequisites are recorded as unavailable rather than omitted.
- [x] Local budget and unseeded pilots declare `ExternalEvidence: false`.
- [x] False-positive and trigger/oracle distinctions are documented.
- [ ] MatrixOne image digest and live SQL client provisioned.
- [ ] SlateDB crate/commit and Cargo.lock provisioned.
- [ ] Dolt release binaries/current-main SHA and Go toolchain provisioned.
- [ ] At least three independent live systems rerun with the same grammar and seed protocol.
- [ ] Upstream confirmation/fix status captured for any Dolt live finding.

## Promotion rule

A result may enter a paper table only when every required per-experiment identity field is present, the raw JSON and log are immutable, the command exits with the declared status, and the result has an explicit interpretation/validity note. Local calibration and controlled mutations remain separate laboratory tables.
