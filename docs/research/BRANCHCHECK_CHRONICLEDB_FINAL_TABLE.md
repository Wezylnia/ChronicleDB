# ChronicleDB Controlled-Mutation Final Evaluation

This table is the final local sensitivity control. It is deliberately separated from external novelty evidence: each row mutates a controlled ChronicleDB scenario and checks that the intended relation remains detectable after semantic reduction.

| Mutation | B0 | B1 | Relation | Result | Reduced witness |
|---|---|---|---|---|---|
| fractured boundary | Pass | Pass | `BC.temporal-boundary` | Pass | yes |
| stale continuation | Pass | Pass | `BC.continuation-state` | Pass | yes |
| missing observer dependency | Pass | Pass | `BC.observer-dependency` | Pass | yes |
| lost recovery lineage | Pass | Pass | `BC.recovery` | Pass | yes |
| non-idempotent lifecycle | Pass | Pass | `BC.lifecycle` | Pass | yes |

The machine-readable source is [`chronicledb-controlled-mutations.json`](../../artifacts/baseline/chronicledb-controlled-mutations.json). The artifact records `irrelevant_frames_removed=true` and `semantic_failure_signature_preserved=true`. These are laboratory sensitivity checks, not claims about an external database implementation or prevalence.
