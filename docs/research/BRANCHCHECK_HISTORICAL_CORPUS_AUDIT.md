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

## Upstream status re-audit (2026-08-15)

This table records the public upstream state re-checked after the local `main` freeze. `Closed` is **not** treated as synonymous with `fixed`: the close reason and linked fix evidence are recorded separately.

| System / issue | Current public state | Fix / confirmation evidence | Audit consequence |
|---|---|---|---|
| MatrixOne #27092 | **Open**; `kind/bug`, `severity/s0`, assigned, milestone 43 | No maintainer confirmation or merged fix is established in the issue thread as of this audit. Reporter follow-ups reproduce the same allocator jump for empty clones and Data Branch creation. | Keep as an open live/historical continuation defect and generic-detectability negative control; do not label it fixed or maintainer-confirmed. |
| MatrixOne #26120 | **Closed / completed** | PR #26310 merged as `ccfcea46981aba349b4fa11445202939f1045c53`; upstream QA and black-/white-box regressions verify historical parent identity and successful downstream DIFF. | Strongest fixed temporal-identity case; safe to use as paired bug/fix evidence. |
| YugabyteDB #29335 | **Closed / completed**; `priority/high`, `2026.1_blocker`, `ga_feature_blocker` | Public issue links DB-19117 but contains no public GitHub comment or fix PR. The issue itself explicitly validates cloned objects/data before later DDL fails. | Creation-evidence caveat can be narrowed: object/data validation is explicit, but complete metadata equality is still not established. Do not invent a fix commit. |
| YugabyteDB #32057 | **Closed / not_planned**; `priority/highest` | No public GitHub fix evidence in the issue. The issue body directly attributes clone-time vector-index `PREPARING` state to a later restart-triggered crash loop. | Retain as recovery-family evidence, but **never call it fixed** merely because it is closed. |
| Dolt #7106 | **Closed / completed** | Maintainer root-cause comment identifies divergent clone-created provider state; PR #7107 merged as `db3472ab54c83bf3891cc5ec8e5526e706a55ddd` and explicitly fixes #7106. | Strong fixed lifecycle/provider case. |
| Neon #506 | **Closed / completed** | Public discussion establishes that an old-LSN branch does not know the correct previous WAL record and that the incorrect value breaks startup/streaming semantics. This audit did not establish a single linked merged fix PR/commit. | Retain as historical boundary/recovery evidence; report closure separately from fix provenance. |
| SlateDB #1902 | **Closed / completed**, but the reporter first closed it because an agent filed it without owner review | A maintainer independently reported finding the same defect; PR #1907 (`Fixes #1902`) merged as `6a131a9ebfd121ca553cb80a08b7b8f2bd142092` with normal and checkpoint-pinned regression tests. | Preserve the unusual provenance in the paper. The paired 0.14.1/fixed artifact is valid regression evidence; do not imply the initial issue closure itself was maintainer acceptance. |

Machine-readable snapshot: `artifacts/external-frozen/historical-upstream-status-20260815.json`.

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

1. **Completed 2026-08-15:** re-check the seven primary issue URLs, public state, and available fix provenance; frozen in `historical-upstream-status-20260815.json`.
2. **Partially completed:** creation evidence is now explicit in the ledger; keep the remaining “complete metadata equality not established” caveats for YugabyteDB #29335/#32057 and Dolt #7106.
3. **Still required for the new live finding:** obtain upstream/independent confirmation or a fix reference for the current Dolt dynamic-clone regression candidate before calling it confirmed.
4. Continue deduplicating by root cause rather than issue count.
5. Preserve all excluded or ambiguous cases in an appendix ledger before submission.
