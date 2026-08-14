# BranchCheck v0.4 — Dolt Current-Main Race-Aware Causal A/B

## Status

This note records the completed repeated causal control for the dynamic-clone continuation regression candidate described in `BRANCHCHECK_V04_DOLT_LIVE_DISCOVERY.md`.

This is **source-causal-reproduced current regression evidence**, not upstream maintainer confirmation.

## Pinned source

Dolt current main was pinned and built from:

`c3b5ce3c67f8677ca08a0a58d8c03cdc95bff8b7`

The BranchCheck witness used a fresh server, repository, file remote, and dynamic clone per repetition.

Minimal semantic trace:

```text
create empty AUTO_INCREMENT source table
→ commit + push
→ DOLT_CLONE(remote, other) returns success
→ clone request completes
→ separate request: INSERT INTO other.test(v) VALUES (99)
```

Reference terminal: success with generated id `1`.

## Unpatched current-main distribution

Twenty independent fresh repetitions:

- `ContinuationRelation = Pass`: **12/20**
- `ContinuationRelation = Fail`: **8/20**
- continuation `Success`: **12/20**
- continuation `Rejected`: **8/20**
- generated id `1`: **12/20**
- no generated id: **8/20**
- every rejected terminal had the same error signature:

```text
Error 1105 (HY000): context canceled
```

This is a stochastic request-lifetime race. The observed **8/20** rate belongs only to this CI sample and must not be reported as a universal failure probability.

## Causal control

The same pinned source tree was modified by restoring only the pre-regression context-lifetime behavior inside `NewSequenceTrackerFromRoots`:

```diff
 gcSafepointController := getGCSafepointController(ctx)
+ctx = context.Background()
 if gcSafepointController != nil {
```

The patch is a causality control, not a proposed production fix.

The same source tree was rebuilt and the same fresh-server / fresh-clone witness was executed twenty more times.

## Patched distribution

- `ContinuationRelation = Pass`: **20/20**
- continuation `Success`: **20/20**
- generated id `1`: **20/20**
- continuation failure: **0/20**
- error terminal: **0/20**

The workflow's race-aware polarity assertion passed.

## Independent release repetition and ordinary-read control

A later independent ten-run release sample added one extra diagnostic **after** the continuation attempt: ordinary `COUNT(*)` / `MAX(pk)` reads of the clone. The read is deliberately performed only after the INSERT attempt so it cannot give asynchronous sequence initialization extra time before the race trigger.

Dolt 2.2.3 again produced:

- **10/10 Pass**;
- **10/10 Success**;
- generated id `1` in all runs.

Dolt 2.3.0 produced a different stochastic split than the earlier 10-run sample:

- **4/10 Pass**;
- **6/10 Fail**;
- all six failures were `context canceled`.

The earlier independent 2.3.0 sample was 6 Pass / 4 Fail. The change from 4/10 failures to 6/10 failures reinforces that these small samples should **not** be interpreted as a stable failure probability.

Crucially, after every one of the six rejected generated inserts in the later sample:

- the clone remained ordinarily readable;
- `COUNT(*) = 0`;
- `MAX(pk) = NULL`;
- the explicit B4 clone-operation grammar baseline remained **Pass**.

Therefore the observed failure is not generic clone unusability. The branch operation has succeeded and ordinary data access still works; the broken path is the later generated-value continuation authority.

## Causal interpretation

The A/B result substantially strengthens the source-level hypothesis:

1. dynamic clone registration constructs destination database global state from the SQL request context;
2. sequence tracker initialization runs asynchronously;
3. the current tracker implementation requires that initialization context to outlive initialization;
4. the 2026-08-03 generic sequence-tracker refactor removed the previous request-cancellation detachment;
5. a short `DOLT_CLONE` request can finish before asynchronous sequence initialization completes;
6. cancellation becomes the tracker's terminal initialization error;
7. a later generated-value continuation surfaces the hidden error;
8. restoring the old cancellation-detached lifetime removes the observed race in 20/20 sampled controls.

The experiment does **not** establish that `context.Background()` is the correct production fix. A production change should preserve any context values needed by GC / sequence state while tying initialization to database lifetime rather than request cancellation.

## Paper interpretation

This result is stronger evidence for **continuation closure** than for oracle novelty.

The clone operation succeeds. A generic branch-operation baseline can therefore pass the clone terminal. A generic state differential baseline can detect the bug once the exact generated-value continuation is supplied. BranchCheck's proposed value is to derive that continuation from the branch capability contract: generated-identifier / sequence authority is latent state that must be usable after the fork request has completed.

The post-failure read control sharpens the distinction: an ordinary observer can still read the valid empty clone after the generated insert is rejected. The failure is therefore not simply a broken database handle or failed clone transaction. It is a hidden **lifetime dependency in continuation state** introduced at branch creation and exposed by a later legal operation.

## Confidence effect

Given:

- second-backend fair-budget evidence on Dolt history-import state;
- an unexpected current-main dynamic-clone continuation regression candidate;
- older-release control;
- current-release/current-main reproduction;
- repeated stochastic characterization;
- ordinary-read-after-failure specificity;
- source-history localization; and
- 20×/20× causal A/B rescue,

the BranchCheck research direction is reasonably assessed at approximately **93/100 conditional**.

The remaining conditions are material:

- no upstream maintainer confirmation yet;
- the new finding emerged from allocator/clone investigation rather than a fully blind campaign;
- broader unseeded search across identity, ownership/dependency, observer, and lifecycle state is still needed;
- the final paper must compare directed witness construction against strong generic stateful exploration under equal budgets, not only curated candidate sets.
