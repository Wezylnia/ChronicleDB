# BranchCheck v0.4 — Dolt Second-Backend Gate and Live Clone-Continuation Regression Candidate

## Status

This document freezes the Dolt evidence obtained after the v0.3 adversarial-baseline gate.

Two distinct results must not be conflated:

1. a **known AUTO_INCREMENT / history-import failure family** gives BranchCheck a second external backend for a fair, capability-derived trigger-budget experiment; and
2. while hardening that experiment, a **different current dynamic-clone continuation regression candidate** was uncovered. The latter is reproduced on Dolt 2.3.0 and pinned current `main`, has a source-level causal explanation, and is removed by a one-line pre-regression lifetime control. It is **not yet upstream-confirmed** and must not be reported as a maintainer-accepted new bug.

The second finding is stochastic: the failure depends on a race between asynchronous sequence-state initialization and SQL request-context cancellation. Single-run PASS/FAIL observations are therefore insufficient; repeated-run distributions are required.

## 1. Why Dolt became the second-backend gate

The MatrixOne v0.3 trigger experiment showed a strong but narrow result: a temporal-identity obligation prioritizes the one source-history mutation that creates same-name object-generation ambiguity.

The next attack required a different backend and a different latent-state class. Dolt is useful because AUTO_INCREMENT state is not merely `MAX(pk) + 1` on the currently visible branch. Dolt maintains database-level sequence state across relevant branch and remote-ref histories. That makes history-import operations such as fetch, pull, and merge a natural capability-derived candidate family.

The candidate selector does **not** hard-code the known failing API `DOLT_PULL`. It asks a semantic question instead:

> Does this history operation change the set/state of refs that the database-global sequence allocator must account for?

For the portable paired experiment the common candidate set is:

- `NoOp` — negative control, does not change sequence-state inputs;
- `FetchOnly` — refreshes remote refs without publishing rows to the current branch;
- `Pull` — imports remote history into the current branch;
- `FetchMerge` — explicit fetch followed by merge.

`FetchHardReset` is excluded from the paired budget because Dolt 2.3.0 cancels the SQL caller context during `DOLT_RESET --hard`. Treating that API-terminal difference as either a semantic success or failure would contaminate the cross-version comparison.

## 2. Correcting a false-positive oracle before using Dolt evidence

The first Dolt prototype incorrectly treated the expected generated identifier as `current visible MAX(pk) + 1`.

That is not Dolt's contract. The sequence tracker considers relevant branch heads and remote refs. Therefore a `FetchOnly` operation may legitimately advance allocator authority even when the current table's visible rows do not change.

BranchCheck was changed to model two separate state dimensions:

- **current visible history** — row count / visible maximum primary key;
- **global sequence-state inputs** — branch and remote-ref histories that constrain the next generated value.

This correction removed a harness-induced false positive in the short-lived CLI experiment and is important to the paper claim: capability-aware relations are useful only if backend-specific semantic differences suppress false positives rather than being forced into one universal reference model.

## 3. Why the Dolt witness must use a long-lived SQL provider

A second false start used separate `dolt sql` processes for setup, fetch/pull, and continuation. That topology can rebuild process-local sequence authority from refs on every command and mask the failure class.

The known long-lived failure topology instead keeps one `dolt sql-server` / database provider alive across:

```text
clone / history operation / later generated insert
```

The executable Dolt adapter therefore runs every candidate in a fresh repository but keeps one SQL provider alive inside that candidate. This is a workload-semantic dimension, not a bug-ID special case: process-local cached or asynchronously initialized authority is itself latent branch state.

## 4. Fair-budget result on Dolt 2.2.3

Pinned release:

- Dolt `2.2.3`;
- published 2026-07-30;
- target commit `670a670ff3dbc12fa1bc68f17e90b85bf2262eab`;
- Linux amd64 release archive SHA-256 `ffafa7cc172cada5f77ca3fb96306545ddac44a111625f75f870306c7f197301`.

Observed portable candidate outcomes in one full real-backend campaign:

| Recipe | Sequence-state relevant? | BC.continuation-state | Generic B2 | Generic B4 | Terminal evidence |
| --- | --- | --- | --- | --- | --- |
| `NoOp` | no | Pass | Pass | Pass | first generated id = 1 |
| `FetchOnly` | yes | **Fail** | Detect | Pass | generated id = 1, expected global continuation = 2 |
| `Pull` | yes | **Fail** | Detect | Pass | generated insert rejected: duplicate primary key `[1]` |
| `FetchMerge` | yes | **Fail** | Detect | Pass | generated insert rejected: duplicate primary key `[1]` |

The important negative control is B4: the history operation itself succeeds. A generic branch-operation outcome checker does not fail until a later continuation exercises the latent allocator state. B2 detects the bug once the generated insert is supplied, so the contribution is **witness selection**, not oracle exclusivity.

### Exhaustive trigger budget

The generic baseline enumerates all `4! = 24` candidate orderings. The relation-guided search enumerates all `3! = 6` orderings within the sequence-relevant class before the `NoOp` control. It does not privilege `Pull` over `FetchOnly` or `FetchMerge`.

Observed exhaustive detection rates:

| Candidate budget | Generic detection | Guided detection |
| ---: | ---: | ---: |
| 1 | **18/24 = 75%** | **6/6 = 100%** |
| 2 | 24/24 = 100% | 6/6 = 100% |
| 3 | 24/24 = 100% | 6/6 = 100% |
| 4 | 24/24 = 100% | 6/6 = 100% |

This is a **modest 25-percentage-point advantage at budget 1**, not a large general speedup. It nevertheless passes the key v0.3 gate: a second external backend and a different latent-state class show a real advantage from a capability-derived candidate class without selecting one exact known failing operation.

## 5. Unexpected current regression candidate discovered while hardening the Dolt gate

While attacking the Dolt adapter itself, a separate failure appeared in the negative-control topology. It does not require remote history to advance and is therefore distinct from the known stale-pull / sequence-refresh family.

### Minimal witness

One long-lived `dolt sql-server` process:

1. create an empty AUTO_INCREMENT table in source;
2. commit and push it to an empty file remote;
3. execute `CALL DOLT_CLONE(remote, 'other')`;
4. allow that SQL request to return successfully;
5. in a **separate SQL request**, execute:

```sql
USE other;
INSERT INTO test(v) VALUES (99);
```

The clone operation itself succeeds and the empty cloned table is addressable. The legal continuation is the first operation that exposes the failure.

The materialized/control expectation is simple and independently supported by Dolt's AUTO_INCREMENT tests: an empty AUTO_INCREMENT table accepts its first generated row as identifier `1`.

### Version observations

Dolt 2.2.3:

- clone returns success;
- separate-request generated insert succeeds;
- generated id = `1`;
- `BC.continuation-state` Pass.

Dolt 2.3.0:

- clone still returns success;
- the later insert sometimes succeeds with id `1` and sometimes is rejected with:

```text
Error 1105 (HY000): context canceled
```

Thus the correct characterization is **request-lifetime race**, not deterministic clone corruption.

## 6. Unbiased release repetition

A dedicated workflow runs fresh server + fresh repository + fresh clone for every repetition and aggregates outcomes without asserting a predeclared polarity.

Ten sampled repetitions produced:

### Dolt 2.2.3

- 10/10 relation Pass;
- 10/10 continuation Success;
- 10/10 generated id `1`;
- no error terminal.

### Dolt 2.3.0

- 6/10 relation Pass;
- 4/10 relation Fail;
- 6/10 continuation Success with generated id `1`;
- 4/10 continuation Rejected;
- all four rejections reported `context canceled`.

Do **not** generalize this sample into a universal 40% failure probability. It is only the observed rate in ten fresh CI repetitions. Its value is qualitative: the older release is stable under the same harness, while the newer release exposes a stochastic request-lifetime failure signature.

## 7. Current-main reproduction

The same minimal probe was run against a source-built, pinned Dolt current-main commit:

`c3b5ce3c67f8677ca08a0a58d8c03cdc95bff8b7`

At least one fresh run reproduced the same terminal:

- clone request: Success;
- later generated insert: Rejected;
- error: `context canceled`;
- `BC.continuation-state`: Fail.

Therefore the symptom is not explained by v2.3.0 packaging alone.

Targeted public issue searches for combinations of `DOLT_CLONE`, AUTO_INCREMENT, sequence tracking, and `context canceled` did not find an exact matching report at the time of this investigation. That is **not proof of novelty**; upstream maintainers may know the defect under different terminology or in a private tracker.

## 8. Source-level causal chain

The source history provides a strong explanation for the race.

### Pre-regression behavior

In Dolt 2.2.3, asynchronous AUTO_INCREMENT tracker initialization explicitly detached its lifetime from the caller request context:

```go
ctx = context.Background()
```

before initialization work continued.

### Regression-introducing refactor candidate

Commit:

`6896f22d4531af000fd5771e4227973757bb8a0b`

from 2026-08-03, associated with PR #11337's sequence-tracker refactor, removed that detachment while converting the AUTO_INCREMENT tracker into the generic `SequenceTracker` abstraction.

The commit's stated purpose is the tracker/context abstraction refactor, not a deliberate request-lifetime semantic change. The targeted PR discussion examined during this work did not reveal an explicit design decision to make tracker initialization die with the SQL request.

### Current dynamic-clone path

Current dynamic database clone registration passes the SQL request context through roughly:

```text
registerNewDatabase(ctx, ...)
→ NewDatabase(ctx, ...)
→ NewGlobalStateStoreForDb(ctx, ...)
→ NewSequenceTrackerFromRoots(ctx, ...)
```

The current sequence tracker initializes asynchronously. Its own implementation states that the caller must ensure the initialization context outlives the initialization method, and later sequence operations wait for initialization and surface its terminal error.

This creates the race:

```text
DOLT_CLONE request starts async sequence-state initialization
        |
        +-- initialization wins before request ends → later INSERT works
        |
        +-- request ends / ctx is cancelled first → initialization records context canceled
                                                  → later INSERT surfaces the latent error
```

That model explains two otherwise confusing observations:

- the minimal short clone request exposes the failure relatively often;
- longer setup requests can mask it by giving asynchronous initialization more time to finish before request cancellation.

## 9. Causal control

A pinned current-main source tree was built twice.

Unpatched:

- the minimal clone-continuation smoke reproduced `context canceled`.

Causal control:

- restore only the old lifetime-detachment line `ctx = context.Background()` at sequence-tracker construction;
- rebuild the same source tree;
- rerun the same smoke.

The patched control succeeded and generated id `1`.

This is strong causal evidence, but the one-line patch is **not proposed as the production fix**. A production design should preserve any context values needed by the sequence/GC subsystem while separating database-lifetime initialization from request cancellation. A DB-lifetime context or an explicit cancellation-detached derived context may be more appropriate than blindly using `context.Background()`.

### Race-aware causal repetition

A stronger A/B workflow is currently required before this evidence is called robust:

- 20 fresh unpatched current-main smokes;
- the same one-line causal control;
- 20 fresh patched smokes;
- require at least one unpatched `context canceled` failure;
- require patched runs to be 20/20 Success, generated id `1`.

The final counts must be recorded here after that gate completes. Until then, the source-causal explanation is strong but the repeated patched control remains an open validation item.

## 10. Why this is a BranchCheck-relevant discovery

The key signature is not “Dolt throws an error.” It is the temporal structure:

```text
fork/clone operation: terminal success
visible clone state: ordinary empty table appears valid
later legal continuation: fails due to hidden async sequence-state initialization
```

A tester that stops at clone success cannot distinguish the good and bad latent states. A generic differential tester can detect the failure **if** it happens to apply the generated-value continuation after clone. BranchCheck's proposed contribution is to derive that continuation from the branch contract: allocator/sequence authority is latent state that must be continuation-complete after a fork.

The minimal scenario now contains an explicit successful `clone` frame for B4 and a separate continuation frame:

- B4 checks the clone operation and should Pass;
- `BC.continuation-state` checks the future generated-value obligation and fails on the race terminal;
- B2 can also detect once that exact continuation is executed.

That is the intended distinction between branch-operation success and continuation semantic closure.

## 11. Relation to known Dolt issue #11387

Do not merge these two failure families in the empirical corpus.

### Known history-import / allocator-refresh family

Requires remote/foreign history to change, followed by fetch/pull/merge and then a generated insert. Observed failures include stale generated identifiers and duplicate primary-key rejection.

### New request-lifetime regression candidate

Requires only dynamic clone creation in a long-lived provider and a later separate-request generated insert. There is no post-clone remote advance, pull, or merge. The characteristic failure is `context canceled`, and the source explanation is asynchronous tracker initialization bound to the clone request context.

They may involve the same broad allocator subsystem, but the triggering topology and root cause are different and should receive different root-cause-family IDs in the bug corpus.

## 12. Research-direction impact

The v0.3 gate asked for:

1. a capability-derived mutation/continuation grammar;
2. fair guided-versus-agnostic search on a second external backend;
3. negative controls;
4. candidate-budget measurements;
5. a live current-upstream campaign not limited to replaying one known issue.

Dolt materially advances all five, but with caveats:

- the fair-budget search space is still small (`4` portable candidates);
- the guided advantage is modest and disappears by budget 2;
- the allocator investigation was motivated by known historical failures, so the live current regression is **not a fully blind fuzzing discovery**;
- the unexpected `NoOp` / short-clone request-lifetime failure is nevertheless distinct from the known witness and forced additional semantic/lifetime reasoning rather than issue-script replay;
- upstream maintainer confirmation is still absent.

If the 20× patched causal repetition is clean, the research-direction confidence can reasonably move above the v0.3 **90/100 conditional** score. It should still remain conditional until a maintainer confirms at least one new report and a broader unseeded campaign exercises more than allocator/history-import state.

## 13. Next gate

After the race-aware causal repetition:

1. freeze exact unpatched/patched distributions;
2. prepare, but do not automatically file, a minimal upstream Dolt issue with the two-request reproducer and causal source evidence;
3. run an unseeded capability-derived campaign across at least three state families, for example:
   - allocator / generated identifiers;
   - historical identity / ownership / dependencies;
   - observer or lifecycle paths;
4. report time/candidates-to-first-failure with generic ordering controls;
5. deduplicate all findings by root-cause family, not issue count;
6. if new findings collapse to known issue templates or generic search performs equally well across broader grammars, downgrade the paper claim again.
