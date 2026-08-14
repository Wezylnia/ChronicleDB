# BranchCheck v0.4 — Dolt Current-Main Race-Aware Causal A/B

## Status

This note records the completed repeated causal control for the dynamic-clone continuation regression candidate described in `BRANCHCHECK_V04_DOLT_LIVE_DISCOVERY.md`.

Classification: **source-causal-reproduced current regression candidate**, not upstream maintainer confirmation.

## Pinned source and witness

Dolt current main was pinned and built from:

`c3b5ce3c67f8677ca08a0a58d8c03cdc95bff8b7`

Each repetition used a fresh server, repository, file remote, and dynamic clone.

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
- every rejected terminal: `Error 1105 (HY000): context canceled`

This is a stochastic request-lifetime race. The observed 8/20 split belongs only to this CI sample and is not a universal failure-rate estimate.

## Causal control

The same pinned source tree was modified by restoring only the pre-regression context-lifetime behavior inside `NewSequenceTrackerFromRoots`:

```diff
 gcSafepointController := getGCSafepointController(ctx)
+ctx = context.Background()
 if gcSafepointController != nil {
```

The same source tree was rebuilt and the same fresh-server/fresh-clone witness was executed twenty more times.

Patched distribution:

- `ContinuationRelation = Pass`: **20/20**
- continuation `Success`: **20/20**
- generated id `1`: **20/20**
- continuation failure: **0/20**
- error terminal: **0/20**

The workflow's race-aware polarity assertion passed.

The one-line change is a **causality control, not a production-fix proposal**. A production solution should preserve required context values while separating database/global-state initialization lifetime from SQL request cancellation.

## Independent release repetitions

The release-level smoke uses fresh server + fresh repository + fresh clone on every repetition and aggregates results without a predeclared 2.3.0 failure rate.

Three independent ten-run samples were collected during the investigation.

### Dolt 2.2.3

- sample A: **10/10 Pass**
- sample B: **10/10 Pass**
- sample C: **10/10 Pass**
- successful continuation generated id `1` in every run

### Dolt 2.3.0

- sample A: **6 Pass / 4 Fail**
- sample B: **4 Pass / 6 Fail**
- sample C: **8 Pass / 2 Fail**
- every observed failure used the same `context canceled` terminal

The changing split reinforces that these samples must be reported as repeated stochastic evidence, not as a stable failure probability.

## Ordinary-read-after-failure control

One independent ten-run sample added an ordinary clone read **after** the continuation attempt, deliberately avoiding any pre-INSERT delay that could make the race easier to win.

For every rejected generated insert in that sample:

- the clone remained readable;
- `COUNT(*) = 0`;
- `MAX(pk) = NULL`;
- B4 clone-operation grammar remained **Pass**.

This rules out the simple explanation that the clone operation failed silently or left an unusable database handle. The broken path is the later generated-value continuation authority.

## Causal interpretation

The evidence supports this chain:

1. dynamic clone registration constructs destination global state from the SQL request context;
2. sequence tracker initialization runs asynchronously;
3. the tracker requires initialization context to outlive initialization;
4. the 2026-08-03 generic SequenceTracker refactor removed the previous cancellation detachment;
5. a short `DOLT_CLONE` request can finish before async initialization completes;
6. cancellation becomes the tracker's terminal initialization error;
7. a later generated-value continuation surfaces that hidden error;
8. restoring the old detached lifetime removes the observed failure in the 20/20 sampled causal controls.

The experiment does **not** establish that `context.Background()` is the correct production implementation. It establishes that request-cancellation lifetime is causal in the observed failure.

## Paper interpretation

This is stronger evidence for **continuation closure** than for oracle novelty.

The clone operation succeeds. A generic B4 branch-operation checker therefore passes the creation terminal. A generic state differential baseline can detect the bug once the exact generated-value continuation is supplied. BranchCheck's proposed value is to derive that continuation from the fork contract: generated-identifier / sequence authority is latent state that must remain usable after fork creation completes.

The post-failure read control sharpens the claim: ordinary reads can remain correct while one future semantic capability is already poisoned.

## Confidence effect and remaining gate

Together with the MatrixOne temporal-identity experiment and the frozen Dolt 2.2.3 fair-budget gate, this supports the current **93/100 conditional** assessment.

Remaining conditions:

- no upstream maintainer confirmation;
- the discovery arose during allocator/clone investigation rather than a fully blind campaign;
- fair search grammars remain small;
- broader held-out identity, dependency/ownership, lifecycle, and recovery campaigns remain future work.

The v0.4 prototype is now under **engineering freeze**: no additional framework plumbing should be added unless it advances one of those preregistered research gates.
