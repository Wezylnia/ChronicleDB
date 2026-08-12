# A2 novelty-kill report — stable ancestry routing

Status date: 2026-08-12  
Candidate: A2  
Working title: **Boundary-Stable Ancestry Routing for Branchable MVCC Stores**  
Disposition: **NARROW / CHALLENGER, not primary**

## 1. Research question

ChronicleDB keeps branch creation cheap by giving each writable child a fixed logical parent boundary. A current read first checks branch-local MVCC state; a local miss recursively follows parent histories at their fixed branch-base boundaries. Deep ancestry therefore creates read amplification for inherited and negative reads.

The v1.1 P3B prototype memoizes, per key, the history/boundary at which recursive lookup resolved. The route is deliberately derived, in-memory, non-authoritative, and contains logical history/boundary identity rather than a physical page pointer. Local writes invalidate the key route; reopen starts empty and rebuilds; compaction does not change the logical route target.

The novelty attack asks whether this is a genuinely new database mechanism or an MVCC-specific adaptation of established layered lookup/caching techniques.

## 2. Claims killed by prior art

The following claims MUST NOT be made.

1. **First system to observe deep branch/read amplification.** BranchBench directly measures the cheap-branch / deep-read trade-off across branchable DBMSs.
2. **First to traverse parent/ancestor layers on a miss.** Neon timelines, VHD/VHDX differencing disks, qcow2 backing chains, OverlayFS, and version-first dataset representations all use parent/lower-layer fallback in some form.
3. **First to cache a resolved lower/ancestor lookup.** Linux OverlayFS performs upper/lower lookup, applies whiteout shadowing, and caches the combined lookup result in the overlay dentry. Generic resolved-layer memoization is therefore not novel.
4. **First to use tombstone-like shadowing in layered lookup.** OverlayFS whiteouts explicitly hide matching lower objects.
5. **First to accelerate ancestry/history traversal with summaries or indexes.** Git commit-graph generation numbers and changed-path Bloom filters, Decibel hybrid branch-membership indexes, and dataset-version materialization/partitioning work already occupy this design space.
6. **First online storage/retrieval trade-off for versions.** Principles of Dataset Versioning and ORPHEUSDB/LyreSplit already formulate and optimize recreation/retrieval versus storage/materialization trade-offs.
7. **Universal read-speedup claim.** Existing P3B/P3BR evidence contains weak regions; depth-4 and low-amortization cases must remain visible.

## 3. Strongest prior-art attacks

| Prior work / system | Same problem? | Same semantics? | Same mechanism? | What it kills | Surviving difference |
|---|---:|---:|---:|---|---|
| **BranchBench (2026)** | Yes | Partly | No | Novelty of the problem/motivation | Benchmark does not provide ChronicleDB's routing mechanism |
| **Neon PageServer** | **Yes** | **Very close** | Partly | Novelty of fixed-boundary ancestor fallback | Official storage design describes recursive ancestor fallback at `ancestor_lsn`; targeted review did not identify a per-key resolved-ancestor route equivalent to P3B |
| **Linux OverlayFS** | **Yes** | Partly | **Very close** | Generic memoized layered lookup, shadow/whiteout novelty | Lower-layer mutation while mounted is unsupported/undefined; no MVCC historical boundary or version visibility contract |
| **VHD/VHDX differencing disks** | Yes | Partly | Close | Parent-chain read-amplification novelty | Block-level parent identity/chain, not writable-history MVCC at frozen per-edge commit boundaries |
| **qcow2 backing chains** | Yes | Partly | Close | Backing-chain fallback novelty | Block-image semantics, not MVCC branch visibility or logical version identity |
| **Decibel** | Yes | Partly | Close design space | Lineage traversal + branch indexing novelty | Relational fragment/bitmap representations rather than per-key fixed-boundary MVCC route cache |
| **TardisDB** | Partly | Close | Partly | Branch-aware MVCC visibility novelty | Branch bitmap + version chain; not the same derived route-to-resolved-history mechanism |
| **Git commit-graph / changed-path Bloom** | Partly | No | Close primitive | Generation/Bloom/summary novelty | Commit/path history rather than DB visibility at frozen branch-base boundaries |
| **ORPHEUSDB / Principles of Dataset Versioning** | Yes at trade-off level | No | Alternative | Online retrieval/materialization novelty | Dataset-version materialization/partitioning rather than branch-current key routing |
| **Minuet / ForkBase / MatrixOne / Dolt-style root structures** | Yes at architecture level | Partly | Alternative | Claim that ancestry walking is unavoidable | Direct-root / shared-tree / copied-metadata designs are strong architectural baselines |

## 4. The critical cross-domain killer: OverlayFS

Linux OverlayFS is the strongest attack on the broad P3B mechanism. It has an upper/lower layered namespace; an upper object hides a lower object; a whiteout records deletion and hides the corresponding lower name; lookup across underlying directories is combined and cached in the overlay dentry. Modern OverlayFS also supports multiple lower layers.

Therefore the generic statement

> "on a local miss, resolve the lower/ancestor object once and cache where it came from"

is prior art and cannot be A2's novelty claim.

However OverlayFS also documents that modifying underlying filesystems while they participate in a mounted overlay is not allowed and leads to undefined behavior. ChronicleDB's relevant case is different: ancestor histories remain writable and continue advancing, while a child is semantically frozen to the historical parent boundary captured on each branch edge.

That difference is necessary for A2 to survive, but it is not by itself enough to claim a new general caching technique.

Primary source: https://docs.kernel.org/filesystems/overlayfs.html

## 5. The closest database semantics: Neon

Neon's PageServer is the closest database-system prior art found in this attack. Its official storage design says `LayeredTimeline` is ancestor-aware and returns data from the ancestor timeline when the requested page is absent on the current timeline. A child is bound to an `ancestor_lsn`, and later history on the parent beyond that branch point is ignored for child reads.

This directly closes broad claims around:

- fixed parent boundary;
- ancestor-aware database reads;
- parent continuing beyond the child's visible branch point;
- recursive inherited-state reconstruction.

The surviving distinction is consequently not the recursion semantics. It can only be the **derived per-key logical route that bypasses repeated ancestry traversal while remaining valid under fixed historical boundaries, local shadow/tombstone semantics, compaction and reopen**.

A targeted review of the official storage documentation and code search did not surface an equivalent per-key resolved-ancestor route cache. This is evidence-bounded and MUST NOT be written as proof that Neon or all other systems lack one.

Primary source: https://github.com/neondatabase/neon/blob/main/docs/pageserver-storage.md

## 6. VHDX/qcow2 make chain lookup non-novel

Microsoft's VHDX specification defines differencing disks where reads start at the latest child and walk toward the parent if needed. Parent identity is explicitly checked through linkage metadata. QEMU qcow2 similarly reads unallocated clusters from a backing file and permits backing-file chains.

These systems make both the parent-chain performance problem and stable parent linkage classical. They are not MVCC stores, but they prevent A2 from claiming novelty for chain lookup, parent identity, or generic route-to-parent concepts.

Primary sources:
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/83f6b700-6216-40f0-aa99-9fcb421206e2
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/b6332a98-624d-46b8-bd0e-b77b573662f9
- https://www.qemu.org/docs/master/interop/qcow2.html

## 7. Surviving A2 claim

The broad "online ancestry acceleration" claim is weakened. The strongest surviving formulation is:

> **A rebuildable, non-authoritative, per-key logical route for writable MVCC history trees whose ancestor histories continue to advance while each child edge remains frozen at a historical visibility boundary; route validity is defined by MVCC value/tombstone/no-visible-version semantics and does not depend on physical page identity, so compaction and restart can safely discard/rebuild the index.**

This is a narrow systems composition claim, not a claim that memoization, layered lookup, Bloom filters, generation numbers, or ancestry indexes are new.

### Required semantic obligations

A defensible A2 mechanism must demonstrate all of the following together:

1. **Boundary stability:** later writes in an ancestor beyond the captured branch-base boundary cannot change a cached child result.
2. **Local shadow correctness:** a local value cuts ancestry fallback.
3. **Tombstone correctness:** a visible tombstone cuts fallback and must not be confused with a missing local version.
4. **Historical visibility:** routing resolves against the ancestor's fixed historical boundary, not its latest state.
5. **Logical identity only:** no physical page/file pointer is recovery authority.
6. **Compaction safety:** physical relocation/rewrite cannot change routing semantics.
7. **Reopen safety:** routes may be thrown away and rebuilt; an empty/missing index preserves correctness via recursive fallback.
8. **Mutation invalidation:** a local key mutation invalidates any route whose leaf-local shadow status changed.
9. **Negative lookup soundness:** a cached negative result must remain valid under the same fixed-boundary visibility proof.

## 8. What current P3B proves — and what it does not

Current P3B/P3BR already has valuable evidence:

- real branch read path rather than a synthetic arithmetic model;
- inherited value, tombstone and negative lookup checks;
- local-shadow handling;
- logical history/boundary route rather than physical pointers;
- compaction-preserved results;
- reopen-empty/rebuild behavior;
- independent child-process repetitions;
- depth-8 repeated smoke with roughly 2x median P99 improvement, while weaker runs are preserved.

But current P3B is still closest to **simple per-key memoization**. Given OverlayFS and related layered-storage prior art, that implementation is not a strong enough centerpiece for a paper whose main claim is a new routing algorithm.

## 9. Required baselines if A2 continues

Do not compare only against recursive ancestry. At minimum:

1. recursive fixed-boundary fallback;
2. **simple per-key memoization** — current P3B should become a baseline, not automatically the proposed method;
3. eager inherited-state materialization / flattening;
4. Decibel-style or equivalent branch-membership summary where adaptable;
5. Git-style changed-key probabilistic summary (false positives allowed, false negatives forbidden);
6. a range/segment summary variant;
7. proposed MVCC-boundary-aware mechanism, only if it is materially stronger than simple memoization.

Measurements must include:

- depth 1/2/4/8/16;
- uniform and Zipf/skew;
- inherited-hit / tombstone / negative-read mix;
- divergence and local overwrite rate;
- cold build versus warm reads;
- cache/index bytes and key bytes;
- route invalidation/update cost;
- branch lifetime / reads-to-amortize;
- reopen/rebuild cost;
- compaction;
- mean/P95/P99;
- a low-depth/low-reuse negative region.

## 10. Kill condition for the narrowed candidate

A2 should be retired as a standalone paper if any of the following occurs:

1. a direct database/storage prior art is found that already implements per-key resolved-ancestor routing at a fixed historical MVCC branch boundary with equivalent tombstone/negative semantics; or
2. after simple memoization is promoted to a fair baseline, a more MVCC-specific mechanism does not produce a meaningful Pareto improvement; or
3. benefit disappears outside deep/high-reuse synthetic cases; or
4. the only surviving difference from OverlayFS/Neon is terminology rather than a distinct validity/invalidation/compaction obligation.

## 11. Current score

These are research-priority/readiness scores, not acceptance probabilities.

| Dimension | Score |
|---|---:|
| Novelty | **15/20** |
| Importance | **14/15** |
| Prior-art defensibility | **15/20** |
| Experimental evidence | **18/20** |
| ChronicleDB fit | **14/15** |
| Publication readiness | **8/10** |
| **Total** | **84/100** |

Previous working score was about 89/100. The reduction is deliberate: BranchBench strengthens the problem, but OverlayFS/VHDX/qcow2 and Neon substantially weaken ownership of the mechanism.

## 12. Decision

**NARROW, do not kill yet.**

A2 remains technically useful and scientifically interesting, but it is no longer the strongest immediate paper candidate. Current P3B should be treated as a strong **memoization baseline**. Further implementation is justified only if we prototype a mechanism whose contribution is specifically the MVCC-boundary validity/summary problem and which beats simple memoization on a preregistered Pareto matrix.

Until that stronger mechanism exists, A2 ranks below A1 and likely below A8 on novelty defensibility.

## 13. Sources used in this kill pass

Repository full texts:

- `02-decibel-the-relational-dataset-branching-system.txt`
- `03-orpheusdb-bolt-on-versioning-for-relational-databases.txt`
- `05-versioning-in-main-memory-database-systems-from-musaeusdb-to-tardisdb.txt`
- `07-tardisdb-extending-sql-to-support-versioning.txt`
- `10-minuet-a-scalable-distributed-multiversion-b-tree.txt`
- `11-generic-version-control-configurable-versioning-for-application-specific-requirements.txt`
- `14-branchbench-aligning-database-branching-with-agentic-demands.txt`
- `15-version-control-system-for-data-with-matrixone.txt`
- ForkBase and related immutable/versioned-index sources in the same corpus.

External primary/official sources checked in the final pass:

- Linux kernel OverlayFS documentation: https://docs.kernel.org/filesystems/overlayfs.html
- Neon PageServer storage design: https://github.com/neondatabase/neon/blob/main/docs/pageserver-storage.md
- Microsoft VHDX overview and parent locator specification: https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/83f6b700-6216-40f0-aa99-9fcb421206e2 and https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/b6332a98-624d-46b8-bd0e-b77b573662f9
- QEMU qcow2 format: https://www.qemu.org/docs/master/interop/qcow2.html
- Git commit-graph documentation: https://git-scm.com/docs/git-commit-graph
- ORPHEUSDB publication page: https://www.microsoft.com/en-us/research/publication/orpheusdb-bolt-on-versioning-for-relational-databases/
- Principles of Dataset Versioning: https://arxiv.org/abs/1505.05211
- TardisDB publication record: https://portal.fis.tum.de/en/publications/tardisdb-extending-sql-to-support-versioning/

