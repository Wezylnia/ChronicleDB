# BranchCheck v0.2 — Historical Evidence and Baseline Gate

## Status

This document records the second implementation gate for BranchCheck. It is deliberately narrower than a paper result: the historical cases below are curated issue transcripts, not an unbiased live campaign, and the generic baselines operate over the recorded witness traces rather than independently discovering those traces.

The purpose of this gate is to answer two implementation questions before investing in a large framework:

1. can the same backend-neutral relation engine express failures from independent branch implementations; and
2. does the engine distinguish branch-specific relations from failures that a generic state/recovery baseline would already detect?

## Core changes from v0.1

- `B2.generic-state-differential` compares ordinary visible state/outcomes only on generic read/mutation frames.
- `B3.generic-recovery` compares ordinary visible state/outcomes on restart/recovery frames.
- `BC.recovery` is an explicit negative-control relation: restart failures should often also be detectable by B3.
- creation evidence can be incomplete. B0/B1 return `Inconclusive` instead of silently treating unreported creation state as equal.
- temporal-boundary obligations are capability-declared. BranchCheck no longer assumes every backend carries every state component from the source boundary.

The last change matters for ChronicleDB itself: a historical branch inherits data/dependency state from a fixed parent boundary but intentionally owns a fresh branch-local commit-sequence namespace. Requiring its local continuation sequence to equal the parent's branch point would be a false positive.

## Curated historical transcript set

| System | Issue | Primary relation | Generic negative control | Evidence status |
| --- | --- | --- | --- | --- |
| MatrixOne | #27092 | continuation state | B2 can detect the post-clone insert divergence | open, `kind/bug`, severity/s0 |
| MatrixOne | #26120 | temporal identity/boundary | B2/B3 do not express the branch-specific ancestry relation | closed/completed, `kind/bug` |
| YugabyteDB | #29335 | continuation/mutation viability | B2 detects later DDL failure | closed/completed, high priority, GA blocker |
| YugabyteDB | #32057 | recovery closure | B3 detects restart crash-loop | closed, `kind/bug`, priority/highest |
| Dolt | #7106 | lifecycle closure | generic read/mutation and recovery baselines do not express DROP-after-clone | closed/completed, bug/customer issue |
| Neon | #506 | historical boundary + recovery | B3 detects startup failure; boundary relation diagnoses mixed LSN state | closed/completed |
| SlateDB | #1902 | observer/dependency closure | B2 primary-reader path passes; alternate supported observer fails | closed/completed, bug |

Sources are stored in the case definitions so reports preserve the exact issue URL. This is not a claim that seven cases are representative of all branch bugs.

## Current recorded-trace result

The v0.2 campaign contains 7 cases from 5 independent systems.

- BranchCheck relation layer detects/classifies: **7/7**.
- B2/B3 generic baselines detect: **4/7**.
- BranchCheck-only under the recorded operation classes: **3/7**.
- B0 detects: **0/7** among cases with sufficient creation-value evidence.
- B1 detects: **0/7**, but several cases are intentionally `Inconclusive` because the issue report does not contain exhaustive creation-time schema/metadata evidence.

These numbers are a harness sanity result, not publishable effectiveness numbers. The witness traces were selected from known bugs, so trace-discovery difficulty is not measured here.

## What the negative controls teach us

BranchCheck should not claim uniqueness for every branch failure.

- YugabyteDB #29335 is visible to an ordinary state/outcome differential checker once the right later DDL operation is executed.
- YugabyteDB #32057 and Neon #506 are visible to a generic restart/recovery oracle once the relevant restart/startup path is exercised.

The strongest surviving BranchCheck-specific examples in this small set are therefore not generic future failures. They are relations that require branch-specific knowledge about:

- historical object identity and ancestry (`MatrixOne #26120`);
- lifecycle validity of a created clone (`Dolt #7106`); and
- equivalence across supported observation paths that resolve shared parent state differently (`SlateDB #1902`).

This is exactly the distinction the next live campaign must test without selecting traces from known bugs.

## Real ChronicleDB adapter gate

The research test project contains a conditional full-repository integration test. When the ChronicleDB engine project is present, it executes this real sequence:

```text
main write old value
  -> record historical boundary
  -> main advances
  -> create historical branch at old boundary
  -> materialize independent reference database
  -> compare creation state
  -> parent advances again
  -> apply same child/reference continuation
  -> compare current + branch snapshot + branch historical observer
  -> close and reopen both worlds
  -> compare recovered state
  -> close branch handle
  -> delete branch
```

The capability profile intentionally declares only `data`, `metadata`, and `dependencies` as source-boundary components. ChronicleDB's branch-local sequence namespace is not treated as a copied parent continuation counter.

## Next hard gate

Do not generalize the framework further until:

1. the full-repository ChronicleDB integration test is green in CI;
2. one external executable adapter runs a backend without encoding a bug-specific relation;
3. B2 and BranchCheck receive the same operation budget on at least one generated campaign;
4. a branch-specific relation detects a failure that the generic baseline does not detect under that fair budget; and
5. at least one historical issue is rediscovered from a relation template rather than a fixed reproducer script.

If the external campaign shows that B2/B3 plus ordinary branch grammar subsume the relation layer, stop and revisit the paper claim instead of expanding the codebase.
