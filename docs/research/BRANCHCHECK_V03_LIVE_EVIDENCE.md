# BranchCheck v0.3 — Live Evidence, Adversarial Baselines, and Revised Claim

## Status

This document freezes the evidence obtained after the v0.1 micro-spike and v0.2 historical-transcript gate. It also records a deliberately negative result: once exact failing witnesses are supplied, broad generic baselines can detect every case in the current curated historical set.

That negative result changes the intended paper claim. BranchCheck must not claim that generic stateful/recovery/branch testing is intrinsically unable to detect these failures. The surviving research question is whether branch contracts provide semantic obligations that can **direct witness construction and diagnose hidden branch-state divergence more efficiently and systematically** than relation-agnostic exploration.

The evidence below is a research gate, not a publishable effectiveness result.

## 1. Claim killed by the adversarial baseline attack

The current curated historical set contains seven cases from five independent systems. When the known witness trace is already supplied, the combined B0–B5 baseline suite produces:

- BranchCheck relation layer detects/classifies: **7/7**;
- at least one generic/adversarial baseline detects: **7/7**;
- strict BranchCheck-only historical cases: **0/7**.

The baseline suite is:

- **B0 — creation values:** visible values immediately after fork/clone;
- **B1 — creation visible state:** values + schema + reported visible metadata when evidence is complete;
- **B2 — generic state differential:** ordinary read/mutation outcomes and visible state;
- **B3 — generic recovery:** restart/recovery outcomes and visible state;
- **B4 — generic branch grammar:** legal branch-history/lifecycle operations such as DIFF/DROP without understanding hidden lineage relations;
- **B5 — generic observer smoke:** exercise advertised observation APIs independently against a materialized reference.

Therefore the following broad statement is **rejected**:

> Known branch correctness failures require BranchCheck because generic testers cannot detect them.

A fairer upper-bound statement is:

> Once the correct witness operation/API is supplied, ordinary differential, recovery, branch-grammar, or observer-smoke checking can expose many branch failures. BranchCheck must justify itself through semantic obligation construction, witness selection, diagnosis, or search efficiency rather than oracle exclusivity.

## 2. Live MatrixOne continuation experiment: negative control

The executable SQL adapter was run against a MatrixOne image reporting:

- server version: `8.0.30-MatrixOne-v4.1.4`;
- Docker image ID: `sha256:7161e9d672fc55c5330af2acaf3afe29d2d2dfe9273a1748bca4c77f8dac8bf8`;
- pulled registry digest observed in CI: `sha256:16ef37311b43c6882ed8242bf38b1e281d853d4f4f6163830fe10773c9fe011d`.

The adapter creates an AUTO_INCREMENT source, native clone, and independently materialized control, then applies the same INSERT continuation.

Observed result:

- cloned rows at creation equal the reference rows → **B0 Pass**;
- clone AUTO_INCREMENT metadata already differs at creation → **B1 Detect**;
- the later INSERT produces a visible state divergence → **B2 Detect**;
- `BC.continuation-state` also fails (`10001` versus `4`).

This is a useful real branch-state failure and regression seed, but it is **not BranchCheck-specific evidence**. It remains in the evaluation as a negative control demonstrating that the framework does not label every branch bug as uniquely relational.

## 3. Live MatrixOne historical-identity experiment

The stronger live experiment creates a historical snapshot of `parent_t`, then reuses the same source name for a new object generation before creating a data branch from the snapshot.

On the same v4.1.4-labeled image, one run observed:

- historical parent object: `object:272539`;
- current same-name object: `object:272540`;
- child row: historical `1:snapshot-row`;
- branch bookkeeping/protection dependency: current generation `object:272540`.

The ordinary child state is correct, but hidden lineage/dependency identity belongs to a different object generation.

The executable relation produced:

- **B0 creation values: Pass**;
- **B1: Inconclusive** because the experiment does not pretend ordinary application-visible metadata is an exhaustive clone-state oracle;
- **B2 generic state differential: Pass**;
- **B3 recovery: NotApplicable**;
- **B4 generic branch grammar: Pass**;
- **B5 observer smoke: NotApplicable**;
- **BC.temporal-boundary: Fail**.

The B4 operation is not synthetic. The adapter executes MatrixOne's legal `DATA BRANCH DIFF child AGAINST parent` command. In the observed run the command itself succeeded and returned:

```text
parent_t    INSERT    2    current-row
```

Thus the boundary relation exposes a hidden lineage inconsistency even when the ordinary child read and a legal branch-history operation both succeed.

### Upstream validation of the invariant

MatrixOne PR #26310 fixes issue #26120 by preserving the source snapshot suffix while resolving the parent relation for branch bookkeeping. The upstream regression test explicitly requires both branch `p_table_id` and branch-protection `obj_id` to equal the **historical parent object ID**, not the later same-name object's ID.

This is important evidence that `BC.temporal-boundary` is not a ChronicleDB-specific invented invariant: it matches the semantic invariant encoded by the upstream maintainer fix.

Do not infer exact source chronology from the `v4.1.4` version label alone. The live container still exposes the wrong hidden identities while its DIFF behavior differs from the original issue transcript, so image labels and issue chronology are recorded separately.

## 4. Live SlateDB observer experiment and paired fix control

A small Rust probe uses public SlateDB APIs with exact dependency `slatedb = 0.14.1`:

1. create a parent DB;
2. write and flush 128 keys to SSTs;
3. create a zero-copy clone;
4. read the clone through `DbReader` and `Db` independently;
5. emit only canonical observer counts/errors to the C# BranchCheck relation engine.

On published `0.14.1`:

- `Db`: **128/128** keys readable;
- `DbReader`: **0/128** keys readable because parent-resident SSTs are resolved incorrectly;
- B2 primary read path: Pass;
- `BC.observer-dependency`: Fail.

However the adversarial **B5 generic observer smoke** also detects this divergence. Therefore this bug is a good observer-closure regression test, but not evidence that a semantic relation is strictly more powerful once every advertised observer is already exercised.

The same probe is then rebuilt against SlateDB PR #1907's merged fix commit:

`6a131a9ebfd121ca553cb80a08b7b8f2bd142092`

On that commit:

- `Db`: 128/128;
- `DbReader`: 128/128;
- B5: Pass;
- `BC.observer-dependency`: Pass;
- no BranchCheck failure remains.

This paired **buggy FAIL → fixed PASS** control strongly reduces the risk that the observer relation itself is a false-positive oracle.

### Provenance correction

Issue #1902 should not be summarized merely as a normal closed bug. The reporter closed the issue because it had been filed by an agent without owner review. A maintainer then independently rediscovered the same defect, and PR #1907 containing the fix was merged. Empirical reporting must preserve that distinction.

## 5. Fair-budget trigger-selection experiment

The next attack asks a different question from the exact-witness baseline table:

> If a tester has a small budget of legal source-history mutations, can a contract-derived relation direct it toward the mutation that exposes a hidden branch-boundary violation?

For the MatrixOne historical-identity setup, five legal mutation recipes are executed on the real backend between snapshot creation and historical branch creation:

1. `NoOp`;
2. `UpdateSourceRow`;
3. `CreateUnrelatedObject`;
4. `RecreateUnrelatedObject`;
5. `RecreateSourceSameName`.

All recipes use the same branch/reference construction and the same temporal-boundary relation. Only `RecreateSourceSameName` violates the boundary invariant on the tested image:

| Recipe | BC.temporal-boundary | B2 | B4 |
| --- | --- | --- | --- |
| NoOp | Pass | Pass | Pass |
| UpdateSourceRow | Pass | Pass | Pass |
| CreateUnrelatedObject | Pass | Pass | Pass |
| RecreateUnrelatedObject | Pass | Pass | Pass |
| RecreateSourceSameName | **Fail** | Pass | Pass |

The generic comparison does not use one arbitrary RNG seed. The harness enumerates all `5! = 120` possible candidate orders. With one violating recipe, a relation-agnostic ordering has the following exhaustive detection probability under a candidate budget:

| Candidate budget | Generic orderings detecting | Generic rate | Relation-guided rate |
| ---: | ---: | ---: | ---: |
| 1 | 24/120 | **20%** | **100%** |
| 2 | 48/120 | **40%** | **100%** |
| 3 | 72/120 | **60%** | **100%** |
| 4 | 96/120 | **80%** | **100%** |
| 5 | 120/120 | **100%** | **100%** |

The guided selector uses the temporal-identity precondition — reuse of the source object's logical name across generations — rather than an issue number or expected object ID.

This result is encouraging but intentionally modest. It is a **five-recipe controlled search space on one backend**, not evidence of a general 5× testing improvement.

## 6. Real ChronicleDB adapter

The full-repository CI runs a real ChronicleDB branch/reference integration path:

```text
historical boundary
→ native historical branch
→ independent materialized reference
→ parent divergence
→ branch/reference continuation
→ current + snapshot + historical observers
→ close/reopen
→ recovered-state comparison
→ branch deletion
```

The scenario passes the capability-aware BranchCheck relations and is compiled against the actual ChronicleDB engine project, not a copied mock. This is a false-positive/sanity backend rather than evidence about an external implementation defect.

## 7. Revised research claim

The implementation results reject the earlier broad detection claim. The current defensible direction is:

> Branchable databases create capability-specific semantic obligations over lineage, dependencies, continuation state, lifecycle, recovery, and observation paths. BranchCheck compiles those obligations into directed multi-history witnesses and explicit diagnostic relations. Generic testing can expose many failures once the right witness is supplied; the research question is whether obligation-guided construction finds and explains them with less search and less backend-specific bug scripting.

This is narrower than “generic testers miss branch bugs,” but scientifically stronger because it survived explicit counter-baselines.

## 8. Current confidence

Research-direction score after the executable adversarial gates: **90/100 conditional**.

Why it is not higher:

- the historical set is curated and selected from known bugs;
- exact-witness baselines cover 7/7 current historical cases;
- the fair-budget trigger experiment uses only five hand-designed MatrixOne recipes;
- only one external backend currently has a relation-guided trigger-budget campaign;
- there is no new/unreported maintainer-confirmed bug yet;
- the MatrixOne image's exact source chronology is not inferred from its version label.

Why it remains at the serious-primary threshold:

- the relation core is backend-neutral and passes a real ChronicleDB integration test;
- upstream maintainer fixes independently validate the historical-boundary and observer/dependency invariants;
- live MatrixOne shows a hidden boundary inconsistency while B0, B2, and B4 pass;
- the first exhaustive candidate-budget experiment shows a real contract-derived selector can isolate the violating semantic-ABA recipe without an issue-specific expected value;
- SlateDB provides a real buggy/fixed paired control for relation validity.

## 9. Next kill gate

Do not expand BranchCheck into a large framework yet. The next gate must answer whether the trigger-selection result generalizes.

Required before a larger implementation investment:

1. define a mutation/continuation grammar from capabilities, not historical issue scripts;
2. run fair-budget guided-versus-relation-agnostic search on **at least one second external backend**;
3. include negative controls where the generic search is equally good or better;
4. measure candidate executions / time-to-first-violation rather than only final bug counts;
5. attempt a live campaign on current upstream versions without seeding known failing recipes;
6. if guided search does not provide a repeatable advantage or new diagnostic value across systems, downgrade or kill the BranchCheck paper direction.

Until that gate is passed, the present code should remain a research spike rather than an engine dependency or large generalized framework.

## 10. Post-v0.4 fairness correction (2026-08-15)

The five-recipe MatrixOne budget in Section 5 is retained as historical experimental provenance, but its original “relation-guided” interpretation is superseded. Re-audit of `MatrixOneTriggerBudgetCampaign` found that the guided path directly selected `RecreateSourceSameName`, the exact recipe already known to violate the historical-identity invariant. Under the later preregistered fairness rules this is target leakage, even though no issue ID was encoded.

Therefore the 20%→100% budget-1 result must **not** be used as fair RQ3 search evidence. Current `main` replaces future MatrixOne runs with a pre-execution 10-recipe grammar and a 3-recipe source-identity-risk class; all recipes inside the risk class are treated uniformly. Fingerprint: `1FA61958C7E97E5EC5BBC8F32D03D99BAAD902F5C360465A7594B8F053B52040`.

This correction lowers current RQ3 evidence from two fair external backends to one (Dolt 2.2.3) until MatrixOne v2 or another fair backend is executed. The temporal-boundary bug evidence itself is unaffected.
