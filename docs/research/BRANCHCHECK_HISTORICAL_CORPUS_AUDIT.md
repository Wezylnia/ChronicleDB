# BranchCheck Historical Corpus Audit (v0.4)

The implementation contains seven curated issue transcripts from five systems. This audit deduplicates them by root cause and separates latent continuation failures from immediately visible branch/lifecycle failures. It is a working audit, not a claim that the corpus is unbiased.

## Classification

- **A — Strong latent continuation:** creation or fork succeeds and the reported initial state is correct or not contradicted; a later legal operation exposes the defect.
- **B — Branch-semantic, immediately observable:** the branch operation or branch metadata is already observably wrong at the boundary.
- **C — Failure-closure:** creation partially fails or leaves cleanup/recovery/orphan state; useful for lifecycle/recovery, weaker for the central beyond-snapshot-equality claim.
- **D — Branch-incidental:** the branch is present in the reproducer but the invariant is not branch-specific. No current case is promoted to D without a separate review.

## Deduplicated case ledger

| System / issue | Root-cause family | Class | Initial state evidence | Legal continuation | BranchCheck relation | Generic supplied-trace result | Status / paper use |
|---|---|---|---|---|---|---|---|
| MatrixOne #27092 | allocator / continuation authority | A | cloned rows and schema reported equal; destination metadata incomplete | first insert after clone generates the wrong ID | `BC.continuation-state` | B2 detects the generated-ID mismatch | historical latent continuation; retain |
| MatrixOne #26120 | temporal identity / boundary | A+B | branch rows are correct; parent metadata/protection points to later same-name object | historical `DATA BRANCH DIFF` | `BC.temporal-boundary` | B0/B1 do not express the ancestry relation; B4 detects supplied command failure | strongest temporal-identity example; retain |
| YugabyteDB #29335 | catalog/ownership continuation | A | cloned objects and data validate | later DDL/create-object operation | `BC.continuation-state` | B2 detects supplied DDL failure | continuation-family support; retain with creation-evidence caveat |
| YugabyteDB #32057 | asynchronous index/recovery dependency | A+C | clone leaves latent index state; complete creation equality is not reported | master restart | `BC.recovery` | B3 detects supplied restart failure | recovery-family support; do not use as a pure snapshot-equality case |
| Dolt #7106 | provider/catalog lifecycle closure | A+C | clone call succeeds; complete creation metadata is not reported | drop cloned database | `BC.lifecycle` | B4 detects supplied lifecycle failure | lifecycle-family support; retain as closure case |
| Neon #506 | mixed historical LSN boundary | A+B+C | requested old boundary is recorded but one component comes from newer source state | compute restart | `BC.temporal-boundary` + `BC.recovery` | B3 detects supplied restart failure | boundary/recovery support; retain with multi-family label |
| SlateDB #1902 | parent-backed observer dependency | A | primary `Db` reads all clone keys; observer-specific dependency is hidden | `DbReader` reads parent-resident SST | `BC.observer-dependency` | B2 passes ordinary read; B5 detects supplied observer failure | strongest observer-closure example; retain |

## Root-cause deduplication

The seven transcripts reduce to six root-cause groups:

1. continuation authority / allocator state (MatrixOne #27092);
2. historical identity and mixed boundary metadata (MatrixOne #26120, Neon #506, related but not duplicate mechanisms);
3. post-clone catalog/ownership viability (YugabyteDB #29335);
4. asynchronous recovery dependency state (YugabyteDB #32057);
5. lifecycle/provider closure (Dolt #7106);
6. observer-specific parent dependency closure (SlateDB #1902).

The MatrixOne identity and Neon LSN cases share the **boundary-consistency family** but are not merged as one root cause: one is same-name object-generation identity, the other is a mixed WAL/LSN recovery boundary.

## Branch-specificity review

| Case | Would the invariant matter without a fork/clone? | Decision |
|---|---|---|
| MatrixOne #27092 | The allocator invariant is ordinary state too, but the clone is what creates the incorrect inherited continuation authority. | Keep as branch-triggered, mark generic-detectability negative control. |
| MatrixOne #26120 | No; the historical object-generation boundary is introduced by snapshot branching. | Primary BranchCheck case. |
| YugabyteDB #29335 | Partly; OID allocation is general, but clone reuse creates the collision state. | Keep as branch-triggered continuation case. |
| YugabyteDB #32057 | Recovery/index state is general, but the clone leaves the latent PREPARING state. | Keep as branch-triggered recovery case, not headline equality evidence. |
| Dolt #7106 | Provider cleanup can fail generally, but the clone-created provider lifecycle is causal in the report. | Keep as lifecycle closure case. |
| Neon #506 | The mixed boundary is specifically introduced by branching at an old LSN. | Primary boundary/recovery case. |
| SlateDB #1902 | Parent-backed zero-copy observer dependency is created by cloning. | Primary observer/dependency case. |

## Baseline interpretation

The supplied-trace union of B0–B5 detects all seven cases. BranchCheck therefore makes no exclusivity claim. The evaluation question is witness construction and diagnosis under a fair capability-derived budget. The current corpus is suitable for taxonomy and replay gates; it is not sufficient by itself for an unbiased discovery-rate claim.

## Audit actions before submission

1. Re-check each issue/PR URL and version against the literature/bug archive.
2. Replace “creation correct” with explicit evidence fields wherever the issue does not report complete creation metadata.
3. Add maintainer confirmation/fix commit fields for any live finding.
4. Do not count a case as an independent root cause when only the symptom or backend differs.
5. Preserve all excluded or ambiguous cases in an appendix ledger.
