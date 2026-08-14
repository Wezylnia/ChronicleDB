# Draft upstream issue — `DOLT_CLONE` can leave AUTO_INCREMENT sequence initialization bound to a completed request context

> **Do not file automatically.** This is a draft for maintainer review after local evidence is frozen.

## Suggested title

`DOLT_CLONE`: first AUTO_INCREMENT insert in a later request can fail with `context canceled`

## Summary

On Dolt 2.3.0 and a source-built current `main`, a database created through `DOLT_CLONE` can intermittently reject the first later AUTO_INCREMENT insert with:

```text
Error 1105 (HY000): context canceled
```

The clone call itself returns successfully. A fresh clone created from an empty AUTO_INCREMENT table is therefore observable and usable at creation, but a generated-value continuation in a separate SQL request can fail.

The failure is stochastic and appears to depend on whether asynchronous sequence-tracker initialization finishes before the `DOLT_CLONE` request context is cancelled.

This is distinct from remote-pull / stale AUTO_INCREMENT bugs: the minimal reproducer below performs no post-clone remote update, pull, fetch, merge, or reset.

## Tested versions

### Control

Dolt 2.2.3

- release target commit: `670a670ff3dbc12fa1bc68f17e90b85bf2262eab`
- 10 fresh repetitions: **10/10 success**
- first generated id: `1` in all 10 runs

### Affected release

Dolt 2.3.0

- 10 fresh repetitions in the same harness: **6 success / 4 failure**
- all four failures: `context canceled`
- all six successes generated id `1`

The 4/10 result is only the sampled CI rate, not an estimate of a universal failure probability.

### Current main

Pinned source:

`c3b5ce3c67f8677ca08a0a58d8c03cdc95bff8b7`

Twenty fresh repetitions:

- **12 success / 8 failure**
- all eight failures: `context canceled`
- all twelve successes generated id `1`

Again, 8/20 is only the sampled rate.

## Minimal topology

The key requirement is one long-lived `dolt sql-server` / database provider with the clone operation and the later generated insert executed in **different SQL requests**.

Pseudo-shell outline:

```bash
mkdir -p source remote
cd source
dolt init

dolt sql-server --host 127.0.0.1 --port 3310 --socket /tmp/dolt-clone-seq.sock &
SERVER_PID=$!
```

Request 1:

```sql
USE source;
CALL DOLT_REMOTE('add', 'origin', 'file:///absolute/path/to/remote');
CREATE TABLE test(
  pk BIGINT PRIMARY KEY AUTO_INCREMENT,
  v INT
);
CALL DOLT_COMMIT('-Am', 'initial empty table');
CALL DOLT_PUSH('origin', 'main');
CALL DOLT_CLONE('file:///absolute/path/to/remote', 'other');
```

Allow that client request to finish. Then run a second client request against the same server/provider:

```sql
USE other;
INSERT INTO test(v) VALUES (99);
SELECT pk, v FROM test;
```

Expected:

```text
pk = 1, v = 99
```

Observed intermittently on 2.3.0 / current main:

```text
Error 1105 (HY000): context canceled
```

Because the race is timing-sensitive, repeat with a fresh server + fresh repositories for each run.

## Additional observation

The failure is sensitive to request lifetime. If extra work is done in the same request after `DOLT_CLONE`, the asynchronous initialization has more time to complete and the failure can disappear. A short clone request followed immediately by a separate generated insert exposes it more often in this harness.

This is consistent with a request-context lifetime race rather than deterministic clone corruption.

## Suspected source path

Dynamic clone registration passes the SQL request context through the destination database/global-state construction path, approximately:

```text
registerNewDatabase(ctx, ...)
  -> NewDatabase(ctx, ...)
  -> NewGlobalStateStoreForDb(ctx, ...)
  -> NewSequenceTrackerFromRoots(ctx, ...)
```

`SequenceTracker` initializes asynchronously and later sequence operations wait for that initialization result.

The current implementation documents that the caller must ensure the initialization context outlives the initialization method. If the clone request context is used directly, request completion can cancel initialization before it finishes.

## Suspected regression point

Dolt 2.2.3's pre-refactor AUTO_INCREMENT tracker detached asynchronous initialization from caller cancellation with:

```go
ctx = context.Background()
```

Commit:

`6896f22d4531af000fd5771e4227973757bb8a0b`

(PR #11337, generic sequence-tracker refactor) removed that detachment while introducing the generic `SequenceTracker` path.

I have **not run a full git bisect**, so this should be treated as a strong suspected introduction point rather than a formally proven first-bad commit.

## Causal control

On the same pinned current-main source tree, I restored only the pre-refactor cancellation-detachment line immediately after obtaining the GC safepoint controller:

```diff
 gcSafepointController := getGCSafepointController(ctx)
+ctx = context.Background()
 if gcSafepointController != nil {
```

Then rebuilt the Dolt binary and reran the same minimal witness.

Race-aware A/B:

### Unpatched current main

- 20 fresh runs
- 12 success
- 8 `context canceled` failures

### One-line causal control

- 20 fresh runs
- **20/20 success**
- **20/20 generated id `1`**
- 0 `context canceled` failures

This strongly implicates request-cancellation lifetime, but I am **not suggesting `context.Background()` as the production fix**.

A production fix likely needs a database-lifetime / cancellation-detached context that still preserves any context values needed by sequence tracking and GC, rather than blindly discarding context values.

## Why ordinary clone-success testing may miss it

`DOLT_CLONE` returns successfully before the failing continuation. Tests that assert only clone completion or immediate visible state can pass.

The failure requires a later operation that touches generated sequence state after the clone request has completed.

A focused regression test should therefore keep one SQL server alive and explicitly separate:

1. `DOLT_CLONE` request;
2. request completion;
3. later AUTO_INCREMENT insert into the cloned database.

## Requested maintainer check

Could you confirm whether sequence/global-state initialization created during dynamic clone is intended to outlive the SQL request that creates the clone? If so, the current request context appears too short-lived for the asynchronous tracker initialization path.
