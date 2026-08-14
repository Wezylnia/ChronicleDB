# BranchCheck v0.4 — Dolt Second-Backend Gate and Live Clone-Continuation Regression Candidate

## Status

This document freezes the Dolt evidence obtained after the v0.3 adversarial-baseline gate.

Two results must remain separate:

1. a **known AUTO_INCREMENT / history-import failure family** provides a second external backend for a fair capability-derived trigger-budget experiment; and
2. a **different current dynamic-clone continuation regression candidate** was uncovered while hardening the experiment. It reproduces on Dolt 2.3.0 and pinned current `main`, has a source-level lifetime explanation, and disappears under a one-line pre-regression causal control. It is **not upstream-confirmed** and must not be reported as a maintainer-accepted new bug.

The second finding is stochastic. The correct claim is a repeated request-lifetime race signature, not a stable failure probability.

---

## 1. Second-backend fair-search gate

Dolt is useful because AUTO_INCREMENT authority is not reducible to `MAX(pk) + 1` on the currently visible branch. Relevant branch heads and remote refs participate in sequence authority, so fetch/pull/merge histories form a natural capability-derived search family.

The candidate selector does **not** hard-code `DOLT_PULL`. It asks:

> Does this history operation change refs that database-global sequence authority must account for?

The portable candidate set is:

- `NoOp` — negative control;
- `FetchOnly` — refresh remote refs without publishing rows to the current branch;
- `Pull` — import remote history into the current branch;
- `FetchMerge` — explicit fetch followed by merge.

`FetchHardReset` is excluded from the portable curve because Dolt 2.3.0 cancels the SQL caller context during `DOLT_RESET --hard`; counting that API-terminal difference would contaminate the paired experiment.

### Oracle correction before evidence use

The first prototype incorrectly modeled the expected generated identifier as `visible MAX(pk) + 1`. Source inspection showed that this is not Dolt's contract. BranchCheck therefore separates:

- **current visible history**, and
- **global sequence-state inputs** derived from branch / remote-ref histories.

That correction removed a harness-induced false positive before Dolt was admitted as paper evidence.

### Long-lived provider requirement

A second false start used separate `dolt sql` processes. That topology can rebuild process-local sequence authority on every command and mask the failure class. The real campaign keeps one `dolt sql-server` / database provider alive across clone/history operation/continuation while using a fresh repository per candidate.

---

## 2. Frozen Dolt 2.2.3 fair-budget result

Pinned release:

- Dolt `2.2.3`;
- published 2026-07-30;
- target commit `670a670ff3dbc12fa1bc68f17e90b85bf2262eab`;
- Linux amd64 archive SHA-256 `ffafa7cc172cada5f77ca3fb96306545ddac44a111625f75f870306c7f197301`.

Observed portable candidate outcomes:

| Recipe | Sequence-state relevant? | `BC.continuation-state` | Generic B2 | Generic B4 | Terminal evidence |
| --- | --- | --- | --- | --- | --- |
| `NoOp` | no | Pass | Pass | Pass | first generated id = 1 |
| `FetchOnly` | yes | **Fail** | Detect | Pass | generated id = 1, expected global continuation = 2 |
| `Pull` | yes | **Fail** | Detect | Pass | generated insert rejected: duplicate primary key `[1]` |
| `FetchMerge` | yes | **Fail** | Detect | Pass | generated insert rejected: duplicate primary key `[1]` |

B4 is the important negative control: each branch/history operation itself succeeds. The latent allocator divergence appears only under the later generated-value continuation. B2 also detects once that continuation is supplied, so the contribution here is **witness selection**, not oracle exclusivity.

### Exhaustive budget curve

Generic search enumerates all `4! = 24` candidate orderings. Guided search enumerates all `3! = 6` orderings inside the sequence-relevant class before the `NoOp` control; it does not privilege `Pull` within the class.

| Candidate budget | Generic detection | Guided detection |
| ---: | ---: | ---: |
| 1 | **18/24 = 75%** | **6/6 = 100%** |
| 2 | 24/24 = 100% | 6/6 = 100% |
| 3 | 24/24 = 100% | 6/6 = 100% |
| 4 | 24/24 = 100% | 6/6 = 100% |

This is a **modest 25-percentage-point advantage at budget 1**, not a universal speedup claim.

The CI workflow now runs Dolt 2.2.3 in an isolated job and asserts this frozen shape directly. Dolt 2.3.0 runs in a separate exploratory job so current request-lifetime failures cannot contaminate the historical fair-budget gate.

---

## 3. Current 2.3.0 contamination note

Current 2.3.0 can mix two different failure families inside the same four-recipe campaign:

- the known stale/global-sequence continuation family, including duplicate-PK terminals after history import; and
- the newer `context canceled` request-lifetime race.

Therefore the 2.3.0 budget curve is **not** used as the headline second-backend search comparison. The RQ3 Dolt result is the isolated, frozen 2.2.3 experiment.

See `BRANCHCHECK_V04_DOLT_230_CONTAMINATION_NOTE.md` for the explicit root-cause separation.

---

## 4. Unexpected dynamic-clone continuation regression candidate

A separate failure appeared in the negative-control topology and does not require post-clone remote advance, fetch, pull, or merge.

Minimal witness in one long-lived provider:

1. create an empty AUTO_INCREMENT table in source;
2. commit and push it to an empty file remote;
3. execute `CALL DOLT_CLONE(remote, 'other')`;
4. allow that SQL request to return successfully;
5. in a **separate SQL request** execute:

```sql
USE other;
INSERT INTO test(v) VALUES (99);
```

Reference terminal: success with generated id `1`.

The clone operation itself succeeds. The ordinary empty clone remains addressable. The later generated-value continuation is the first operation that can expose the failure.

Dolt 2.3.0 sometimes returns:

```text
Error 1105 (HY000): context canceled
```

The correct characterization is a **request-lifetime race**, not deterministic clone corruption.

---

## 5. Repeated release characterization

The repetition workflow uses a fresh server, repository, remote, and clone for every run and aggregates outcomes without asserting a predeclared 2.3.0 failure rate.

Across three independent ten-run samples collected during the investigation:

### Dolt 2.2.3

- sample A: 10/10 Pass;
- sample B: 10/10 Pass;
- sample C: 10/10 Pass;
- successful runs generated id `1`.

### Dolt 2.3.0

- sample A: 6 Pass / 4 `context canceled` Fail;
- sample B: 4 Pass / 6 `context canceled` Fail;
- sample C: 8 Pass / 2 `context canceled` Fail.

These samples demonstrate a repeated stochastic failure signature. They do **not** justify estimating or publishing a universal failure probability.

In the sample instrumented with a post-attempt ordinary read, every failed generated insert was followed by successful clone reads:

- `COUNT(*) = 0`;
- `MAX(pk) = NULL`;
- B4 clone-operation grammar remained Pass.

Therefore the destination is not generically unusable. The failing path is latent generated-value continuation authority.

---

## 6. Current-main reproduction

The same minimal witness was run against source-built pinned Dolt current main:

`c3b5ce3c67f8677ca08a0a58d8c03cdc95bff8b7`

The current-main binary reproduces the same `context canceled` continuation terminal. This rules out a v2.3.0 packaging-only explanation.

Targeted public issue searches for the exact `DOLT_CLONE` + AUTO_INCREMENT + `context canceled` symptom did not find a matching report during this investigation. That is **not proof of novelty**.

---

## 7. Source-level causal chain

In Dolt 2.2.3, asynchronous AUTO_INCREMENT tracker initialization explicitly detached itself from caller cancellation:

```go
ctx = context.Background()
```

Commit `6896f22d4531af000fd5771e4227973757bb8a0b` from 2026-08-03, associated with PR #11337's generic SequenceTracker refactor, removed that detachment.

Current dynamic clone registration passes SQL request context through roughly:

```text
registerNewDatabase(ctx, ...)
→ NewDatabase(ctx, ...)
→ NewGlobalStateStoreForDb(ctx, ...)
→ NewSequenceTrackerFromRoots(ctx, ...)
```

The tracker initializes asynchronously and later sequence operations surface its terminal initialization error. This yields the following race:

```text
DOLT_CLONE starts async sequence-state initialization
        |
        +-- initialization finishes before request ends → later INSERT works
        |
        +-- request ends / ctx cancels first           → initialization records context canceled
                                                       → later INSERT surfaces latent error
```

This also explains why longer setup requests can mask the failure by giving initialization more time before request cancellation.

---

## 8. Completed race-aware causal A/B

The stronger causal experiment is complete.

Pinned source: `c3b5ce3c67f8677ca08a0a58d8c03cdc95bff8b7`.

### Unpatched current main — 20 fresh runs

- `ContinuationRelation = Pass`: **12/20**;
- `ContinuationRelation = Fail`: **8/20**;
- all eight failures: `context canceled`.

### Causal control

The same source tree was changed only by restoring the pre-regression cancellation-detachment line at sequence-tracker construction:

```diff
 gcSafepointController := getGCSafepointController(ctx)
+ctx = context.Background()
 if gcSafepointController != nil {
```

This is a causality control, **not** a proposed production fix.

### Patched current main — 20 fresh runs

- **20/20 relation Pass**;
- **20/20 continuation Success**;
- **20/20 generated id = 1**;
- zero continuation failures.

The race-aware polarity assertion passed. A production fix should preserve required context values while binding initialization lifetime to database/global-state lifetime rather than blindly using `context.Background()`.

The detailed A/B record is frozen in `BRANCHCHECK_V04_DOLT_CAUSAL_AB.md`.

---

## 9. Why this matters to BranchCheck

The signature is:

```text
fork/clone operation: terminal success
ordinary visible clone: readable
later legal continuation: fails due to hidden initialization/lifetime state
```

A tester that stops at clone success cannot distinguish good and bad latent state. A generic differential tester can detect the defect **if** it happens to execute the generated-value continuation. BranchCheck's proposed value is to derive that continuation from the branch capability contract: sequence/allocator authority is latent state that must be continuation-complete after fork creation.

This is evidence for **continuation closure**, not for exclusive oracle novelty.

---

## 10. Relation to known Dolt issue #11387

The history-import allocator family and the new request-lifetime family must receive different root-cause IDs.

### Known history-import / allocator-refresh family

Requires foreign/remote history change followed by fetch/pull/merge and then a generated insert. Observed terminals include stale identifiers and duplicate-primary-key rejection.

### Request-lifetime regression candidate

Requires only dynamic clone creation in a long-lived provider and a later separate-request generated insert. No post-clone remote advance is necessary. The characteristic terminal is `context canceled`, with source evidence pointing to asynchronous tracker initialization bound to request lifetime.

They share an allocator subsystem but not the same trigger topology or root cause.

---

## 11. Research-direction impact and freeze

Dolt now provides:

- a second independent backend for fair capability-derived witness prioritization;
- an explicit example where a naive universal oracle produced a false positive and had to be corrected from backend semantics;
- a live current regression candidate distinct from the known replay target;
- older-release control;
- repeated stochastic characterization;
- ordinary-read-after-failure specificity;
- current-main reproduction;
- source-history localization; and
- a completed 20×/20× causal A/B rescue.

This supports the current **93/100 conditional** assessment.

The conditions remain material:

- no upstream maintainer confirmation;
- the discovery emerged from allocator/clone investigation rather than a fully blind campaign;
- fair grammars remain small;
- broader held-out coverage across identity, dependency/ownership, lifecycle, and recovery is still incomplete.

### Engineering freeze

The v0.4 prototype is complete enough for the current paper claim. Do **not** add framework plumbing merely to increase feature count.

Further engineering is justified only if it materially advances one of these preregistered gates:

1. upstream / independent confirmation of the Dolt regression candidate;
2. a genuinely held-out live campaign spanning additional latent-state families; or
3. a substantially larger fair-budget experiment showing the guided advantage survives beyond four/five hand-enumerated candidates.

Otherwise the next step is paper construction, not more framework expansion.
