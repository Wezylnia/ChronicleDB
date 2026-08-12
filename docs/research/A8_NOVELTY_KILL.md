# A8 Novelty Kill — Erasure-Consistent Branching

Date: 2026-08-12
Status: **NARROW / NEEDS CORRECTNESS PROTOTYPE**
Research-priority score: **81/100** (not an acceptance probability)

## 1. Candidate after the kill pass

The broad A8 story is no longer defensible. Secure deletion in versioned storage, deletion across shared snapshot/clone data, privileged destruction of historical state, bounded delete persistence, WAL cleanup, crash-consistent storage updates, and deletion proofs all have substantial prior art.

The only contribution still worth attacking is narrower:

> For a persistent writable MVCC history tree, compute the **key-specific set of legal historical observers** that can still reconstruct a target value through fixed branch boundaries; distinguish a non-destructive erasure request from an explicitly authorized change to those observer contracts; and acknowledge global erasure only after no engine-controlled recovery authority or physical representation can resurrect the target value after a crash.

This is an evidence-bounded surviving hypothesis, not a novelty claim yet.

## 2. Strongest prior art and what it kills

### 2.1 Peterson et al., FAST 2005 — version-aware secure deletion

*Secure Deletion for a Versioning File System* directly addresses fine-grained secure deletion in a copy-on-write versioning file system. It handles block sharing between versions, explains why deletion must account for sharing dependencies, supports deletion of individual versions, and discusses deletion from all versions as well as out-of-band/off-site use.

Kills:
- first secure deletion for versioned storage;
- first fine-grained deletion from historical versions;
- first need to analyze shared-version dependencies before deletion;
- first secure deletion spanning historical copies.

Source: https://www.usenix.org/conference/fast-05/secure-deletion-versioning-file-system

### 2.2 VMware US10031672B2 — data retention across true clones

The 2015-priority patent *Snapshots and clones in a block-based data deduplication storage system* explicitly motivates “true clones” by retention policies that require **old data to be deleted from all clones/snapshots** while preserving the snapshot/clone abstraction. It uses shared logical/physical structures, reference counts, WAL-backed metadata updates, and replay/crash-recovery machinery.

Kills:
- first selective data removal across retained clones/snapshots;
- first shared-reference accounting for retention-driven deletion;
- first combination of clone deletion/reclamation with WAL-backed storage metadata.

Source: https://patents.google.com/patent/US10031672B2/en

### 2.3 NetApp / WAFL / ONTAP — blocker, destructive override, and secure-purge workflow

Snapshot/clone systems already expose dependency semantics close to `Analyze/Request/Force`:
- clone/base snapshots can pin state and block deletion;
- locked/busy snapshots must be released or dependencies changed;
- destructive autodelete modes can remove snapshots and clones;
- secure purge requires advanced privilege, has explicit phases/status, may require deleting snapshots or moving a common/base snapshot forward, and reports success/failure only after the purge workflow.

Kills:
- first discovery of snapshot/clone blockers;
- first privileged destructive override of historical dependencies;
- first staged secure-purge status/acknowledgement workflow;
- first requirement to advance/delete a retained base snapshot before purging data.

Sources:
- https://docs.netapp.com/us-en/ontap/encryption-at-rest/secure-purge-data-encrypted-volume-concept.html
- https://docs.netapp.com/us-en/ontap/encryption-at-rest/purge-data-encrypted-asynchronous-snapmirror-task.html
- https://docs.netapp.com/us-en/ontap-cli/volume-encryption-secure-purge-show.html
- https://docs.netapp.com/us-en/ontap-cli-9161/volume-snapshot-autodelete-modify.html

### 2.4 Lethe / Lethe+ — delete persistence and WAL purge

Lethe makes delete persistence latency a first-class storage design objective; the extended work includes explicit purging of old WAL records when WAL retention would violate deletion persistence objectives.

Kills:
- first physical-delete latency guarantee;
- first tombstone-to-compaction deletion design;
- first recognition that WAL copies must be purged for a storage-level erasure objective.

Local corpus: `private-docs/literature/papers-txt/29-lethe-a-tunable-delete-aware-lsm-engine.txt`.

### 2.5 Temporal databases — privileged override of historical stability

Teradata transaction-time rows normally remain immutable historical records, but privileged `NONTEMPORAL` operations may delete or modify them when enabled. SQL Server temporal history likewise permits explicit administrative cleanup after changing temporal-history management conditions.

Kills:
- first explicit conflict between historical-query stability and an authorized operation that destroys history;
- first privileged bypass/override of temporal-history preservation.

Sources:
- https://docs.teradata.com/r/Enterprise_IntelliFlex_VMware/Temporal-Table-Support/Basic-Temporal-Concepts/Temporal-Table-Modifications
- https://learn.microsoft.com/sql/relational-databases/tables/manage-retention-of-historical-data-in-system-versioned-temporal-tables

### 2.6 Crash consistency and deletion verification

Crash-safe ordering/publication is a mature systems topic. Retained-checkpoint deletion patents explicitly discuss making deletion recoverable if a crash occurs mid-operation. Separately, proof/certified-deletion literature means A8 must not claim the first deletion acknowledgement, certificate, or verifiable deletion concept.

Examples:
- https://patents.google.com/patent/US20150012567A1/en
- Feng Hao and Dylan Clarke, *How to Delete a Secret* (2012), proof of deletion.
- modern certified/verifiable-deletion literature should be treated as adjacent prior art, not as ChronicleDB's contribution.

## 3. What still appears different

No reviewed work was found that directly specifies all of the following together:

1. **Writable MVCC history tree:** branch ancestors may continue changing, while each child edge retains a fixed historical boundary.
2. **Key-specific observer semantics:** whether an observer blocks erasure depends on the value/tombstone/no-visible-version resolution for the target key, including recursive parent fallback.
3. **Observer-contract scope:** a branch can depend on parent state for key `K` while completely shadowing parent state for key `X`; destroying an entire branch/snapshot is therefore not the minimal semantic revocation.
4. **Independent recovery authorities:** each history may contain authoritative checkpoint/WAL state capable of resurrecting the value after restart.
5. **Fail-closed representation closure:** current/previous/compaction data generations, stale records, WAL and checkpoints must be structurally scanned; incomplete scans prohibit acknowledgement.
6. **Authority-changing force operation:** `RequestErasure` preserves existing observer contracts and blocks; an explicitly authorized force operation may revoke only the contracts necessary for the target key, then perform descendant/authority-safe rewrite and reclamation.
7. **Crash-safe acknowledgement condition:** success means that after any supported crash/recovery point, no legal retained observer and no engine-controlled recovery representation can reconstruct the erased value.

This combination is the surviving A8 hypothesis. Absence of a match in this review is not proof of novelty.

## 4. Critical implementation falsification discovered in ChronicleDB

The current P6 implementation does **not yet implement the surviving claim correctly**.

True GitHub main `5fa3d3835c42e929cef14ab90288e04b9e5c113b` has the improved physical scanner: current, previous, compaction and deleted-branch generations are structurally inspected and incomplete scans fail closed. This is good evidence for representation closure.

However, observer-root content is currently resolved by looking only at `root.ProtectedHistoryId`'s **local** versions at `root.Boundary`.

That is insufficient for a branch snapshot/active historical observer:

```text
Main: K = v1
  |
  +-- A @ Main:t1        (A has no local K)
       |
       +-- A1 @ A:t2     (A1 has no local K)

snapshot/active-view on A1
```

The legal A1 observer resolves `K` through A1 -> A -> Main and sees `v1`. A local-only predecessor check on A1 reports `Absent` and can therefore miss a real erasure blocker.

Consequences:
- existing P6 positive results demonstrate representation enumeration and fail-closed scanning, not observer-exact semantic closure;
- no `ForceErasure` protocol should be implemented on top of the current root classification;
- the next prototype must first solve this semantic oracle problem and must be differential-tested against actual branch reads.

A second semantic gap is the **generic time-travel contract**. ChronicleDB v1.0 explicitly supports ordinary historical reads for every boundary from a history's retention floor through its current sequence. Those boundaries are legal observer contracts even when no persistent snapshot or process-local active root exists. Current P6 blocker classification is root-centric and therefore does not by itself prove that a requested erasure preserves the generic retained time-travel range. For a target key, A8-O1 must enumerate the semantically distinct target-key visibility boundaries in that range (floor/current plus target-key version boundaries) and treat any value-reading observer as a blocker. A force operation that wants to remove such a value would have to explicitly change/advance that public retention contract rather than silently erase beneath it.

## 5. Required next falsification prototype

### A8-O1 — Observer-Exact Erasure Oracle

Research-only; production erase path remains unchanged.

For every legal observer/root `o` and target key `k`, resolve using the same logical semantics as a real historical branch read:

```text
Resolve(history, boundary, key):
    local visible VALUE      -> blocker(value); stop
    local visible TOMBSTONE  -> no inherited value; stop
    no visible local version -> recurse to parent at fixed branch base boundary
```

The oracle must cover:
- Main snapshots / active transactions;
- branch snapshots / historical views;
- branch-base roots;
- nested branches;
- local value shadowing;
- tombstone shadowing;
- pre/post-shadow historical observers;
- deleted branches as physical debt but not legal observer contracts.

### Baselines

1. Whole-history / whole-snapshot blocker model.
2. Shared-block/reference reachability model.
3. Current local-root P6 classifier.
4. Candidate observer-exact recursive classifier.

### Hard correctness gates

- candidate observer result must equal actual historical `TryGet` result for every enumerated observer/key pair;
- no legal observer that returns the target value may be omitted from blockers;
- every reported blocker must have a witness observer/read;
- tombstones must stop parent fallback;
- incomplete physical scan must still fail closed.

### Kill condition

A8 is killed or demoted if:
- observer-exact closure collapses to generic shared-reference reachability / whole-snapshot dependency analysis;
- nested MVCC fallback creates no materially different blocker/revocation set;
- safe force semantics require destroying whole snapshots/branches in the same way as established storage systems;
- crash-safe acknowledgement adds no obligation beyond a straightforward application of existing secure-purge/checkpoint/WAL protocols.

## 6. Score after this kill pass

This score is research priority/readiness, not acceptance probability.

| Dimension | Score |
|---|---:|
| Novelty | 17/20 |
| Importance | 15/15 |
| Prior-art defensibility | 16/20 |
| Experimental evidence | 13/20 |
| ChronicleDB fit | 14/15 |
| Readiness | 6/10 |
| **Total** | **81/100** |

The score fell from the earlier ~87 because OpenZFS/ONTAP, VMware true-clone retention, FAST'05, temporal privileged-history deletion, Lethe/WAL purge, and deletion-proof literature close several broad parts of the story. The remaining hypothesis is narrower and potentially stronger, but the current P6 observer classifier must first be falsified and repaired by a research oracle.

## 7. Decision

**GO — but only for the observer-exact correctness prototype.**

Do not implement production `ForceErasure` yet. The next engineering effort is justified only as a falsification experiment for the surviving semantic difference. If A8-O1 passes and produces blocker/revocation sets that genuinely differ from strong snapshot/clone reachability baselines, then implement the crash-safe authority transition. Otherwise stop A8 and leave P6 as a useful erasure-audit diagnostic rather than a paper candidate.
