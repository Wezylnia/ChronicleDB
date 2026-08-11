# Crash Testing

`ChronicleDB.CrashHarness` is a separate-process durability test. Children terminate with `Environment.FailFast`; normal managed disposal therefore cannot make a test pass accidentally by flushing cleanly.

The parent now rejects a child that exits normally: every configured scenario must actually reach its crash point.

## Transaction scenarios

Every `TransactionFaultPoint` is exercised around WAL append, explicit flush, physical publication, and acknowledgement. The recovered two-key transaction must **always** be atomic: either neither key is visible or both expected values are visible.

- before WAL append: transaction must be absent;
- WAL appended but explicit flush not reached: absent or complete is allowed because the OS may persist buffered data, but partial is forbidden;
- at/after durable flush: transaction must be complete.

`AfterFirstPhysicalPage` additionally kills the child after the first page of a durable multi-key transaction is written. Recovery must replay the complete WAL decision.

## Persistent snapshot scenarios

The harness crashes:

- before snapshot metadata write;
- after metadata write;
- before metadata flush;
- after metadata flush;
- immediately after successful snapshot acknowledgement;
- at equivalent points during snapshot deletion;
- during a later durable parent write after a snapshot already exists.

## History-root scenarios

The generalized-root recovery matrix also covers:

- a missing root record after a durable snapshot create;
- an orphaned active root after a durable snapshot delete;
- an incomplete final root frame;
- a complete corrupt root frame;
- a fault before and after the root metadata flush boundary.

Pre-flush snapshot lifecycle operations may recover as old or new complete state. Post-flush operations must recover the new durable state. Whenever a snapshot exists, its historical contents are verified, not only its metadata count.

## Repetition

`run N` executes every scenario N times with fresh directories. For example:

```powershell
dotnet run --project tools/ChronicleDB.CrashHarness -- run 100
```

This is intentionally heavier than the normal unit suite and should be part of release/soak validation.

## Branch durability scenarios

The harness repeats every `TransactionFaultPoint` through a branch-local transaction. Reopen verifies the two-key branch update is atomic. Before the branch WAL durability barrier, absent or complete outcomes are accepted only where buffered bytes may have reached the OS; after WAL flush, the complete branch transaction is mandatory.

Branch lifecycle persistence is exercised separately:

- crash after branch create intent persistence;
- crash after the branch-base retention root is durable;
- crash after branch activation is durable;
- crash after branch delete intent persistence;
- crash after the branch-base root is deleted during branch deletion;
- crash immediately after a branch snapshot is durably flushed.

Reopen must expose either no branch or one fully active branch according to the durable activation boundary; interrupted deletion must converge to no active branch; and a durably flushed branch snapshot must reopen with identical historical contents. Partially initialized branch state is never accepted as an active history.

## Maintenance scenarios

Process-level failures are injected:

- during retained-history checkpoint record output;
- immediately before retained-history WAL reset;
- immediately after retained-history WAL reset;
- during compacted temporary-page output;
- before compacted data publication;
- after compacted data publication but before cleanup.

Every restart validates both current state and a persistent historical snapshot. A child that exits normally instead of reaching the configured `FailFast` point is itself a harness failure.
