# BranchCheck Paper Pre-Registration

## Working title

**BranchCheck: Finding Latent-State Bugs in Branchable Databases**

Alternative:

**BranchCheck: Testing Semantic Closure of Database Branches**

## One-sentence thesis

A database fork can return successfully and match ordinary visible state while already containing incorrect latent state; BranchCheck derives capability-specific future continuations and cross-history relations that expose those hidden semantic failures.

## Scope discipline

This project is **not** a generic SQL fuzzer, a new version-control model, a universal branch semantics, or a claim that generic differential testing cannot detect branch bugs.

The strongest surviving contribution is narrower:

> Generic stateful or differential testing can detect many branch bugs once the right witness trace is supplied. BranchCheck contributes a branch-specific semantic obligation layer that determines **which histories, observers, and future continuations should be compared**, and uses those obligations to direct witness construction/search.

Any experiment that does not test this thesis should be removed from the paper even if it is technically interesting.

---

# RQ1 — What latent state makes database forks behaviorally wrong after creation?

## Question

Across independently implemented branchable databases, which hidden state classes cause a fork that looks correct at creation to diverge under a later legal operation?

## Candidate taxonomy

1. **Continuation authority**
   - generated identifiers / sequences;
   - object allocator state;
   - asynchronous continuation metadata.
2. **Temporal identity / one-boundary state**
   - historical object identity;
   - lineage / ancestry metadata;
   - protection / retention dependencies.
3. **Observer/dependency state**
   - external/shared physical references;
   - reader vs writer path resolution;
   - checkpoint-pinned dependency maps.
4. **Lifecycle state**
   - delete / detach / nested branch dependencies;
   - stale clone registration / residue;
   - parent-child dependency closure.
5. **Failure/recovery state**
   - partial create/delete/clone authority;
   - restart-visible residue;
   - incomplete recovery metadata.

## Evidence source

A manually reviewed, root-cause-deduplicated historical corpus plus live findings.

## Corpus inclusion rule

Include a case in the **BranchCheck-positive** corpus only if correctness depends materially on branch/fork history, lineage, observer path, continuation state, lifecycle dependency, or recovery authority.

Keep parser crashes, generic deadlocks, generic DDL failures, and performance-only issues in a separate **branch-incidental** set.

## Required reporting fields

- system / version;
- issue / source;
- declared branch contract;
- branch operation;
- latent state class;
- creation-time visible state status;
- later witness operation;
- root-cause family ID;
- generic-baseline detectability;
- BranchCheck relation required;
- fixed / confirmed / triage status.

## RQ1 success condition

At least **3 independent implementation families** must contain branch-specific latent-state failures after deduplication.

Current evidence exceeds this historically, but the final corpus must be audited and frozen before paper submission.

---

# RQ2 — Do branch-specific semantic relations add detection power beyond strong generic baselines?

## Question

Given the same executed history, what failures are detected by BranchCheck relations versus progressively stronger generic oracles?

## BranchCheck relation families

- `BC.continuation-state`
- `BC.temporal-boundary`
- `BC.lifecycle`
- `BC.observer-dependency`
- `BC.recovery`

## Required baselines

### B0 — creation values

Compare visible row/value state only at fork creation.

### B1 — creation visible state

Compare values, schema, and ordinary visible metadata at fork creation.

### B2 — generic state differential

Apply the supplied generic reads/mutations and compare ordinary visible state/outcomes.

### B3 — generic recovery

Compare restart/recovery terminals and ordinary recovered state when the trace contains a restart.

### B4 — generic branch grammar

Execute legal branch/history operations and compare success/failure terminals without understanding branch-specific hidden metadata.

### B5 — alternate-observer smoke

Exercise alternate read/observer paths and detect obvious failures without a branch-specific observer-equivalence relation.

## Important current negative result

On the curated historical 7-case / 5-system transcript set, the union of B0–B5 detects all seven cases once the exact witness trace is supplied.

Therefore the paper must **not** claim oracle exclusivity.

## RQ2 success condition

The paper should show at least one real case where:

- branch operation / creation terminal succeeds;
- B0/B1 and the strongest applicable generic branch-operation check do not expose the latent state at that point;
- a BranchCheck relation diagnoses the violated semantic obligation before or more precisely than ordinary state comparison.

Current MatrixOne historical-identity evidence satisfies this shape for temporal-boundary metadata. The current Dolt dynamic-clone race satisfies the continuation-closure shape but B2 detects it once the exact generated insert is executed.

## Kill condition

If BranchCheck relations never add useful semantic diagnosis beyond a generic state/outcome oracle and cannot improve witness construction, reduce the contribution to an empirical branch-bug study rather than presenting a new testing technique.

---

# RQ3 — Do semantic obligations improve witness search under equal budgets?

## Question

Without being told the exact known failing operation, does capability-derived prioritization find violating traces with fewer candidate executions than an agnostic ordering over the same candidate grammar?

## Fairness rules

1. The candidate grammar must be identical for generic and guided search.
2. The guided selector may use **capability/semantic classes**, not issue IDs or exact failing APIs.
3. Within a guided equivalence/risk class, enumerate or randomize candidates fairly.
4. Count all candidate executions, including controls.
5. Report the full budget curve, not only time-to-first-success.
6. If an API is excluded for portability, record the exclusion and reason before computing the paired curve.
7. Do not tune the selector after observing the target backend's failing candidate without rerunning a held-out or paired design.

## Current controlled results

### MatrixOne historical identity

Five legal source-history mutation recipes; one same-name source-generation replacement violates temporal identity.

Generic exhaustive ordering:

- budget 1: 20%
- budget 2: 40%
- budget 3: 60%
- budget 4: 80%
- budget 5: 100%

Semantic-identity-guided ordering:

- 100% from budget 1.

This is a strong but single-backend controlled result.

### Dolt global sequence state

Portable candidate set:

- `NoOp`
- `FetchOnly`
- `Pull`
- `FetchMerge`

Sequence-state relevance is derived from whether an operation changes refs that the database-global sequence tracker must account for; the selector does not hard-code `Pull`.

On Dolt 2.2.3 the three sequence-relevant recipes violated continuation state in the tested provider topology.

Generic exhaustive `4! = 24` ordering:

- budget 1: 75%
- budget 2+: 100%

Guided `3! = 6` sequence-relevant ordering:

- budget 1: 100%
- budget 2+: 100%

This is a **modest 25-percentage-point budget-1 advantage** on a second backend.

## RQ3 success condition

At least **two independent backend families** must show a positive guided-search advantage under predeclared, fair candidate spaces.

Current MatrixOne + Dolt controlled experiments satisfy this minimum condition, but the final paper should add larger grammars or seeded random campaigns so the result is not dominated by tiny candidate sets.

## Kill condition

If the advantage disappears under larger/fairer grammars or after removing exact-target leakage, report the negative result and drop search efficiency as a central contribution.

---

# RQ4 — Can BranchCheck discover previously unreported current bugs?

## Question

Can the capability-derived witness machinery find new defects on current systems rather than merely replaying historical reports?

## Evidence levels

### Level 0 — synthetic only

Mutation / harness sanity. Never paper evidence by itself.

### Level 1 — known historical replay

Shows executable expressiveness and regression sensitivity.

### Level 2 — current system, known failure family

Useful for validating current relevance but not a new-bug claim.

### Level 3 — unexpected current regression candidate

Not an issue-script replay; reproduced on current binary/source; root cause investigated.

### Level 4 — upstream-confirmed new bug

Maintainer acknowledges, labels, fixes, or independently reproduces the report.

## Current Dolt discovery status

The dynamic `DOLT_CLONE` separate-request AUTO_INCREMENT failure is **Level 3**:

- distinct minimal topology from known pull/remote-refresh issue #11387;
- Dolt 2.2.3 control is stable in sampled runs;
- Dolt 2.3.0 exposes stochastic `context canceled` failures;
- pinned current `main` reproduces;
- source history localizes a request-context lifetime change;
- one-line pre-regression lifetime control removes the sampled current-main race in 20/20 causal repetitions;
- not yet upstream-confirmed.

Do not count it as Level 4 until maintainers confirm it.

## RQ4 success condition for a strong systems-paper evaluation

Preferred target:

- **new confirmed root-cause families in at least 3 systems**, or
- fewer systems only if the bugs are high-impact, independently confirmed, and the search study is substantially broader than the current micro-campaigns.

Known issue replay alone is insufficient.

## Near-term kill / downgrade condition

If broader live campaigns only rediscover known issue templates or produce unconfirmed harness-specific anomalies, keep BranchCheck as a focused empirical/testing paper but downgrade claims about bug-finding generality.

---

# RQ5 — Does the capability profile prevent false positives across different branch semantics?

## Question

Can the same relation engine express materially different backend contracts without forcing one system's implementation choices onto another?

## Required examples

- ChronicleDB child histories use a local continuation-sequence namespace; temporal-boundary checks must not incorrectly require that local sequence to equal the source boundary.
- Dolt AUTO_INCREMENT authority is influenced by branch/remote-ref state and cannot be reduced to visible `MAX(pk) + 1`.
- SlateDB observer equivalence must only compare observers that the profile declares semantically equivalent for the tested clone/checkpoint state.

## RQ5 success condition

Document at least three real cases where a naive universal oracle would produce a false positive or invalid comparison and the capability profile suppresses/corrects it.

Two such corrections already occurred during implementation (ChronicleDB local sequence namespace and Dolt global sequence authority); they should be preserved as threats-to-validity evidence rather than hidden as implementation mistakes.

---

# Systems and roles

## ChronicleDB

Role: controlled, instrumentable backend / clean control / mutation ground truth.

It must not be the sole or primary external validity evidence.

## MatrixOne

Role: SQL-level historical identity and branch lineage; current live controlled search.

## Dolt

Role: independent version-control database; sequence/global-ref continuation state; long-lived provider lifecycle; live current regression discovery candidate.

## SlateDB

Role: lower-level zero-copy clone observer/dependency generality and buggy/fixed regression control.

Its existing 3-candidate guided budget is **not** fair search evidence because the guided candidate was effectively hard-coded; retain it only as oracle validation.

## Neon / YugabyteDB

Role today: historical cross-architecture evidence.

Promote to live evaluation only if reproducible current test environments can be built without distorting the project scope.

---

# Primary experiment table to build for the paper

For every live campaign report:

| Field | Meaning |
| --- | --- |
| backend identity | exact version / commit / image digest |
| capability profile | advertised branch/history operations and semantic components |
| candidate grammar | all legal candidate histories considered |
| guidance rule | semantic class used before execution |
| generic ordering | exhaustive/random control over identical candidates |
| relation | BranchCheck obligation evaluated |
| B0–B5 | strongest applicable generic baseline results |
| creation terminal | whether fork/clone operation itself succeeds |
| later witness | operation exposing divergence |
| candidates-to-first-failure | per ordering/seed |
| budget curve | probability / fraction detected by budget |
| repetitions | for stochastic failures |
| root-cause family | deduplication key |
| issue status | historical / candidate / confirmed / fixed |

---

# Statistical / reporting discipline

- Never report raw issue count as independent evidence without root-cause deduplication.
- For stochastic failures, report numerator / denominator and environment; do not convert a small CI sample into a universal probability.
- Prefer exact exhaustive fractions for tiny candidate spaces instead of pseudo-statistical significance.
- For larger randomized campaigns, predeclare seeds/budgets and report confidence intervals for detection rate / time-to-first-failure where appropriate.
- Separate search efficiency from oracle strength.
- Separate known-bug rediscovery from new-bug discovery.
- Separate source-level causal evidence from maintainer confirmation.

---

# Non-claims that must remain explicit in the paper

BranchCheck does **not** claim:

- stateful generation is new;
- metamorphic/differential testing is new;
- future-trace equivalence as mathematics is new;
- automatic oracle discovery;
- universal branch semantics;
- every branch bug is invisible to generic testing;
- branch correctness can always be reduced to materialized-copy equality;
- one current regression candidate proves broad bug-finding generality.

---

# Current confidence

**93/100 conditional** for the research direction, assuming the frozen evidence remains reproducible.

Why the score increased from v0.3:

- a second backend now has a fair capability-derived budget comparison;
- the implementation work produced an unexpected current-main latent continuation regression candidate rather than only historical replay;
- the candidate survived older-version control, current-source reproduction, stochastic repetition, source localization, and a repeated causal control.

Why it is not higher:

- no upstream confirmation yet;
- the new bug emerged from allocator/clone investigation, not a fully blind campaign;
- the search grammars are still small;
- broad current-system evidence across identity, ownership/dependency, lifecycle, and recovery remains incomplete;
- acceptance-level evaluation requires more new root-cause families and larger fair-budget campaigns.

---

# Next mandatory gate

Do **not** expand the framework indiscriminately.

The next work must be one of:

1. a broader **unseeded live campaign** generated from capability profiles across at least three latent-state families; or
2. upstream confirmation / independent reproduction of the current Dolt regression candidate; or
3. a larger fair-budget experiment showing that semantic guidance survives beyond four/five hand-enumerated candidates.

If none of these materially strengthens the paper, stop adding engineering surface and write the paper around the evidence already obtained.
