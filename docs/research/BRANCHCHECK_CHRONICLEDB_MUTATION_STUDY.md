# ChronicleDB Controlled Mutation Study

This is a controlled laboratory study over the real ChronicleDB historical round-trip adapter. It is a relation-sensitivity check, not external novelty evidence. The baseline first performs a real fork, continuation, observer, restart, and delete sequence. The study then applies a semantic latent-state mutation to the recorded observation and evaluates the unchanged BranchCheck relations.

## Acceptance rule

For every mutation:

1. B0 creation values remains `Pass`.
2. B1 creation visible state remains `Pass`.
3. The declared BC relation for the mutated obligation becomes `Fail`.
4. The test does not branch on a mutation name to change the oracle; only the relation ID is the expected semantic contract.

## Results

| Controlled mutation | Latent state represented | Expected relation | Result |
|---|---|---|---|
| fractured-boundary | metadata boundary advances beyond the declared historical boundary | `BC.temporal-boundary` | PASS |
| stale-continuation | branch continuation token diverges after a legal mutation | `BC.continuation-state` | PASS |
| missing-observer-dependency | historical observer loses a parent-backed dependency | `BC.observer-dependency` | PASS |
| lost-recovery-lineage | restart reconstructs a branch without its lineage | `BC.recovery` | PASS |
| non-idempotent-lifecycle | branch deletion retry is rejected while reference succeeds | `BC.lifecycle` | PASS |

The five mutations are exercised by `ChronicleDbControlledMutationStudyTests`. The same test also adds irrelevant read/restart frames and confirms that `BranchScenarioReducer` removes them while preserving the semantic failure signature.

## Interpretation boundary

These mutations demonstrate that the relation families are sensitive to representative latent-state defects in the ChronicleDB testbed. They do not establish that an external DBMS has the defect, that the mutation is realistic for every backend, or that generic baselines cannot detect the terminal outcome. Those claims require the provisioned external campaigns and fair candidate-space experiments.
