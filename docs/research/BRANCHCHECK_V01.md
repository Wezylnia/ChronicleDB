# BranchCheck v0.1 Micro-Spike Contract

## Status

BranchCheck v0.1 is a research-tool spike. It is deliberately downstream of ChronicleDB engine semantics and does not change v1.0 transaction, branch, persistence, recovery, retention, or maintenance behavior.

## Research claim under test

Creation-time equality is a necessary but insufficient branch correctness oracle. A branch can match a reference world's visible values, schema, and visible metadata at creation yet violate a capability-specific obligation under a later legal operation.

BranchCheck tests that narrow claim. It does **not** claim a new generic fuzzing method, a new bisimulation/refinement theory, a universal branch model, or automatic discovery of test oracles.

## v0.1 pipeline

```text
CapabilityProfile
    -> applicable ForkObligations
    -> witness trace
    -> canonical observations
    -> relation result
```

The v0.1 code is intentionally backend-neutral. Real ChronicleDB/external adapters are a later gate.

## Micro relations

### BC.continuation-state

A supported continuation operation must preserve backend-declared continuation state relative to the reference world. Candidate real witnesses include generated identifiers, sequences, and other post-fork allocators.

### BC.temporal-boundary

For a historical fork, adapter evidence for `data`, `metadata`, `dependencies`, and `continuation` must resolve to the declared source boundary. This relation is not applicable when the backend does not advertise historical forking.

### BC.lifecycle

When branch deletion is a supported lifecycle operation, a valid branch and its reference world must agree on the canonical lifecycle outcome under an equivalent dependency state.

### BC.observer-dependency

Observers explicitly declared equivalent by the capability profile must agree with their corresponding reference observations. This is intended to expose path-specific dependency resolution failures rather than requiring all backend read APIs to be universally equivalent.

## Baselines in v0.1

- `B0.creation-values`: compare only visible values at creation.
- `B1.creation-visible-state`: compare values, schema fingerprint, and visible metadata fingerprint at creation.

The synthetic mutation campaign is required to demonstrate that B0/B1 pass while each targeted BranchCheck relation fails. This is a harness sanity check, not empirical paper evidence.

## Next kill gate

Do not expand BranchCheck into a large framework until all of the following hold:

1. one ChronicleDB adapter and one external backend adapter can express the same relation vocabulary;
2. historical bugs are rediscovered without bug-specific reproducer logic in the relation engine;
3. `B2` generic stateful differential and `B3` generic recovery baselines are implemented against the same generated histories;
4. at least one branch-specific relation class exposes a failure missed by the generic baseline under a fair trigger budget;
5. capability profiles suppress legitimate backend differences instead of reporting them as false positives.

If the generic baselines subsume the BranchCheck relations, stop implementation and revisit the paper claim before adding more code.
