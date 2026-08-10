# v0.7 testing methodology

ChronicleDB uses independent validation layers because no single kind of test is sufficient for a storage engine.

## Required layers

1. **Unit tests** — identifiers, transaction state, visibility, index behavior, snapshot catalog.
2. **Format tests** — golden encodings, round trips, bounds, reserved fields, checksums, compatibility.
3. **Corruption tests** — metadata generations, data pages, overflow chains, WAL framing/LSNs, snapshot lifecycle framing.
4. **Generated/property tests** — deterministic visibility cases and generated transaction/history operations.
5. **Reference-model differential tests** — compare ChronicleDB with the intentionally simple managed SI oracle.
6. **Concurrency tests** — readers during writers, same-key conflict storms, independent writers, multi-key atomic visibility, worker scaling, read/balanced/write-heavy profiles.
7. **Recovery tests** — incomplete/aborted WAL transactions, idempotent replay, torn append regions, snapshot registry recovery.
8. **Process crash tests** — child-process `FailFast` at durability-sensitive fault points.
9. **Workload replay** — configurable deterministic seed/round/worker runner that records reproduction parameters on failure.
10. **Branch differential tests** — compare Main, sibling branches, historical branch boundaries, aborts, and retained branch snapshots with an independent branching reference model.
11. **Branch integration/concurrency tests** — fixed-parent fallback, tombstones, source-snapshot independence, nested depth, own-writes, sibling isolation, disjoint concurrent writers, and same-key FCW conflicts.
12. **Soak/release runs** — repeat crash harness and workload runner for substantially larger counts than CI smoke tests.

## Deterministic reproduction

`ChronicleDB.WorkloadRunner [seed] [rounds] [workers]` reports all three values on failure. The operation grammar includes transactions, puts/deletes, conflicts, persistent snapshot create/delete, historical reads, restart, and a concurrent disjoint-write phase.

The correctness test project separately runs seeded historical differential workloads across restart.

## Visibility property test

The unit suite executes two million deterministic combinations of version state, commit sequence, tombstone flag, and visibility boundary against the authoritative visibility rule. This is intentionally independent of page/WAL code.

## Release commands

```powershell
dotnet test ChronicleDB.slnx

dotnet run --project tools/ChronicleDB.WorkloadRunner -- 42 10000 8

dotnet run --project tools/ChronicleDB.CrashHarness -- run 100
```

Release evidence should preserve failing seeds, raw logs, runtime/OS details, and benchmark JSON rather than reporting only a green summary.


## v0.7 branch gate

The v0.7 suite must demonstrate that branch creation does not copy the parent data file; local puts/deletes remain private; parent and siblings cannot drift a branch base; local tombstones suppress fallback; branch-local Snapshot Isolation uses a stable `StartSequence`; branch snapshots and historical reads match the reference model; nested lookup is bounded; and reopen accepts only local physical data covered by published branch commit metadata. Full independent branch-WAL crash testing remains a v0.8 gate.
