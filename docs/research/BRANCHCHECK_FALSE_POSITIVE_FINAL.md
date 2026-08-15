# Final False-Positive and Oracle-Conflict Table

The campaign preserves every observed outcome. A semantic-class violation outside the preregistered obligation class is not silently promoted to a BranchCheck discovery; it is reported as a false positive/model conflict and remains in the denominator.

| System/version | Candidate class | Observed result | Classification | Why it is retained |
|---|---|---|---|---|
| MatrixOne v2 | `TruncateSource` (ordinary) | temporal-boundary violation | false-positive/model conflict | The candidate is outside the three source-identity-risk recipes; it tests whether the oracle is over-broad. |
| MatrixOne v2 | `RecreateSourceSameNameSchemaVariant` (identity) | temporal-boundary violation plus B4 detection | known failure with generic overlap | Both signals are retained; this is not a BC-only claim. |
| Dolt 2.3.0 | `NoOp`, `StatusOnly`, `BranchList`, `LogLocal` (observer controls) | all four report continuation violations | false-positive/model/version conflict | `AllViolationsInsideSequenceRelevantClass=false`; controls are not discarded and explain the version-specific polarity. |
| SlateDB 0.14.1 | three clone-reader candidates | unreadable clone objects | known observer-dependency failure | Every violation is inside the frozen dependency class. |
| SlateDB fixed `6a131a9e` | all eight candidates | no violation | negative control | Confirms paired polarity and keeps the same grammar denominator. |
| External unseeded replay | all five frozen versions | 37 no-failure, 3 known-failure, 94 duplicate-root-cause, 26 false-positive, 0 new-root-cause, 0 ambiguity, 0 harness | complete outcome ledger | The replay includes all 160 seed/version runs and never filters by the eventual outcome. |

The Dolt 2.3.0 result is therefore a robustness/version-model observation, not a universal Dolt failure probability. The external replay is explicitly a deterministic replay over frozen per-candidate observations; it is external evidence for classification and protocol integrity, not 160 fresh backend executions.
