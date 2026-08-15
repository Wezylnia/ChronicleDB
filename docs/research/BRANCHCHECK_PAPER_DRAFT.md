# BranchCheck: Beyond Snapshot Equality — Testing the Future Semantics of Database Branches

This is the evidence-locked manuscript draft. It follows the execution order used in the research plan: Evaluation first, then Methodology and design, and only then Introduction and Abstract. Numeric claims must be regenerated from the frozen artifacts before submission.

## 1. Evaluation

### 1.1 Research questions

We evaluate whether executable branch obligations help construct and diagnose continuation witnesses under equal candidate budgets (RQ3), whether the same obligations span different branch architectures (RQ2/RQ5), and whether the resulting relations explain real external failures without pretending to be an exclusive oracle (RQ1/RQ4).

### 1.2 External fair-search evidence

MatrixOne v2 uses ten source-history recipes and three identity-risk recipes. It reports three violations and a budget-one generic/guided detection rate of `0.30/0.6667`; generic B0/B2/B4 controls remain visible. Dolt 2.2.3 uses ten portable history-import recipes, six sequence-relevant, and reports six violations entirely inside that class with `0.60/1.00` at budget one. SlateDB uses eight observer/dependency candidates; the buggy crate reports three dependency-class violations and the fixed commit reports none. Dolt 2.3.0 is a deliberate robustness result: all ten candidates, including four controls, violate the continuation relation, so it is not counted as a clean fair-positive search result.

All candidate sets were frozen before execution and carry fingerprints in the manifest. The complete curves, versions, logs, and SHA-256 archives are in `artifacts/external-frozen/`.

### 1.3 Unseeded classification

The external unseeded artifact contains 46 complete candidate observations from five external versions. A preregistered 32-seed, budget-four uniform replay yields 160 runs: 37 no-failure, 3 known-failure, 94 duplicate-root-cause, 26 false-positive/model-conflict, and zero new-root-cause, oracle-ambiguity, or harness outcomes. The replay is external evidence for protocol and classification, but it is explicitly not 160 fresh backend reruns.

### 1.4 Dolt robustness and causal sweep

The robustness workflow covers releases 2.2.3/2.3.0, pinned current main, and three context-lifetime controls over delays 0, 1, 5, 10, 50, 100, and 500 ms, with 100 repetitions per cell. The final 35-cell matrix reports 0/681 relation failures for 2.2.3, 221/681 for 2.3.0, 300/688 for unpatched current main, 0/700 for `context.Background()`, and 0/674 for `context.WithoutCancel`; harness failures (19, 19, 12, 0, 26) remain in the ledger. Each cell records outcome, relation, generated id, server health, harness failures, and timing. A failed cell or control is preserved rather than converted into a probability claim.

### 1.5 ChronicleDB controls and reduction

Five controlled ChronicleDB mutations pass their intended relations: temporal boundary, continuation state, observer dependency, recovery, and lifecycle. External semantic witnesses reduce to one candidate frame while preserving relation id and polarity; this is a semantic reduction, not a claim about internal backend event counts. The local mutation artifact remains laboratory sensitivity evidence.

### 1.6 Validity and limitations

The strongest positive fair-search evidence is Dolt 2.2.3 and the paired SlateDB observer study. MatrixOne demonstrates identity-boundary value but has generic overlap. Dolt 2.3.0 demonstrates a version/model conflict that is scientifically useful precisely because the false positives are retained. Upstream Dolt issue #11387 independently documents the same pull/auto-increment mechanism; its open state is not interpreted as a fix or as a universal failure probability.

## 2. Methodology

For each backend we freeze a capability-derived grammar, semantic class, seed ledger, candidate budget, timeout, command, and version identity before reading outcomes. A uniform scheduler samples every candidate ordering without selecting a named historical recipe. A relation-guided scheduler may prioritize the complete semantic class but treats members uniformly. B0–B5 generic baselines are measured separately from BC relations. Every JSON result is archived with logs and a SHA-256 manifest; the validator fails closed on digest, structure, or semantic-polarity changes.

The external unseeded stage consumes complete candidate observations and applies deterministic Fisher–Yates permutations. This separation makes the limitation explicit: the backend executions are external, while the 32-seed ordering ledger is a reproducible replay rather than a hidden post-hoc rerun.

## 3. System design

BranchCheck separates (i) capability profiles, (ii) legal candidate grammars, (iii) reference/branch execution adapters, (iv) relation evaluation, (v) generic baselines, and (vi) evidence provenance. The composition root selects concrete adapters; relation semantics do not depend on a managed baseline index. Each external adapter exports candidate-level observations so that fair-budget analysis and trace reduction consume the same immutable evidence.

## 4. Obligation taxonomy

The frozen taxonomy covers continuation-state preservation, temporal identity stability, boundary consistency, observer closure, dependency closure, mutation viability, lifecycle closure, and recovery closure. A backend advertises only the families it can execute. A relation is meaningful only when its capability precondition and reference construction are recorded; unsupported families are not scored as failures.

## 5. Representative examples

* MatrixOne source recreation preserves ordinary values and generic branch grammar but violates a historical temporal boundary after same-name recreation.
* Dolt history import changes sequence authority: the branch can complete ordinary SQL while its generated continuation token diverges from the reference.
* SlateDB clone readers expose a dependency-closure failure when compacted SST objects are unavailable; the merged fix restores all eight observer candidates.
* ChronicleDB controlled mutations show that the same relation engine can detect each obligation family in a known local scenario.

## 6. Related work and novelty boundary

The paper positions BranchCheck against differential testing, snapshot/clone storage, temporal and branched indexes, crash/recovery testing, and property-based database testing. Its narrower novelty boundary is the explicit comparison of relation-agnostic versus semantic-class-guided witness construction under frozen legal candidate spaces, with generic baseline overlap and false-positive classes reported instead of hidden. The prior-art and attack-test dossiers remain separate research records; no “first” claim is made without a direct comparison.

## 7. Introduction

Database branching is often validated at creation time: rows, schemas, and visible metadata match. That check is insufficient when later operations depend on latent sequence authority, object identity, observer dependencies, or recovery lineage. BranchCheck treats a branch as a future-semantic contract and asks which legal continuation exposes a violation. The goal is not to replace database fuzzing, but to make the obligation and the witness-construction budget explicit.

## 8. Abstract

Database branches can match their parent at creation while carrying latent state that makes a later supported operation diverge. BranchCheck expresses such future semantics as executable obligations and compares uniform candidate exploration with relation-guided exploration under equal budgets. Across frozen MatrixOne, Dolt, and SlateDB artifacts, the method covers temporal identity, continuation authority, and observer/dependency closure while preserving generic baseline overlap and false-positive outcomes. A ten-candidate Dolt 2.2.3 campaign reports six sequence-class violations and a budget-one generic/guided rate of 0.60/1.00; an eight-candidate SlateDB paired study reports 3/3 dependency violations in the buggy crate and none after the fix. A separate 32-seed replay records all outcome classes over complete external candidate observations. These results support semantic witness construction as a useful, bounded technique, not a universal oracle or probability claim.

## 9. Conclusion

The evidence supports a modest conclusion: branch obligations make latent-state witness construction measurable and portable across heterogeneous systems when grammars, controls, and provenance are frozen in advance. The method is strongest when a complete semantic class is available and weakest when generic state divergence overlaps the relation or when a version exposes control-class conflicts. Future work should seek an upstream fix or independent fresh-main rerun and additional non-identity families without expanding the framework merely for feature count.
