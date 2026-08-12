# A1 Shadow-Aware Retention — Final Targeted Novelty Verification

Status date: 2026-08-12

This is the final targeted novelty-kill pass before Holdout-A. It is intentionally narrower than the earlier 65-source literature review. It asks only whether the surviving A1 claim is already directly disclosed by a paper, patent, database project, or storage design inspected in the local corpus and the final 2026 web/patent search.

## Surviving claim under attack

The candidate claim is **not** generic branch-aware garbage collection, snapshot reachability, clone shadowing, interval MVCC garbage collection, reference counting, or reclaimable-space accounting.

The surviving systems claim is the combination of:

1. a **key-specific semantic propagation** of retained observer requirements through fixed writable-branch ancestry;
2. a local visible **value or tombstone stopping inherited fallback** for that `(observer, key)` while unshadowed keys continue to propagate to the parent boundary;
3. construction of a **minimum sufficient cross-history MVCC retained projection** for the declared retained observer set;
4. a **descendant-first durable recovery-authority transition** before ancestor predecessors made unnecessary by the new projection may be reclaimed; and
5. measured logical-to-physical realization with restart/crash observer preservation.

The implementation and paper must describe this as MVCC observer-semantic marking/reachability. Generic graph reachability itself is not claimed as novel.

## Strongest prior-art constraints

| Prior art | What it already establishes | What must not be claimed | Direct match to surviving A1? |
| --- | --- | --- | --- |
| SAP HANA HybridGC / interval GC (SIGMOD 2016; US10102120B2) | Per-record versions not visible to any active flat-history snapshot can be reclaimed; global-minimum timestamp GC is not the strongest baseline. | First exact/per-record/snapshot-aware MVCC GC. | **No direct match found.** No writable branch ancestry or key-specific descendant fallback propagation. |
| TardisDB (SIGMOD 2021, DOI 10.1145/3448016.3452767) | Branch-aware tuple visibility, branch bitmaps, MVCC-style version chains and branch lineage semantics. | First branch-aware MVCC visibility/versioning. | **Closest semantic composition threat**, but the inspected work does not specify A1's minimum retained cross-history projection or descendant-first durability protocol. |
| HeliosDB branch-aware MVCC GC technical note (Zenodo 10.5281/zenodo.19242034, 2026) | Descendant branch creation LSNs, retention and active snapshots are combined into a per-branch scalar GC horizon and LSM compaction filter. | First branch-aware MVCC GC; first descendant-protected GC; first branch-aware LSM GC. | **No direct match found.** It protects descendants through branch-level horizons rather than per-key shadow stopping. |
| MatrixOne VCS for Data (arXiv:2604.03927, 2026) | Branch-protect snapshots, immutable/MVCC data versioning, branch/clone lifecycle and GC integration. | First branch/snapshot-protected GC. | **No direct match found.** Protection is snapshot/object/version-control oriented rather than A1's per-key semantic projection. |
| Neon pageserver GC, SlateDB checkpoints, trine-kv durable branch pins | Durable branch/snapshot/checkpoint pins prevent GC from deleting ancestor storage still needed by retained views. | First durable fork pin; first GC-aware branch checkpoint. | **No direct match found.** These designs retain the pinned horizon/manifest/fork boundary; the inspected docs do not remove parent MVCC predecessors selectively when descendant keys become semantically shadowed. |
| Rodeh, *B-trees, Shadowing, and Clones* (ACM TOS 2008); clone/snapshot patents | Generic COW clone sharing, writable-snapshot shadowing, reference/reachability based release and crash-aware checkpointing are old ideas. | Novel clone shadowing, COW reachability, refcount reclamation, or generic descendant object unlinking. | **No direct match to the MVCC observer claim.** This is the strongest argument for framing A1 as domain-specific semantic projection rather than generic shadow/reachability. |
| MVCC search-tree tracing GC / snapshot-list patents | Mark/reachability and snapshot-driven reclamation of immutable/MVCC search-tree elements are established. | First marking/reachability GC or first snapshot-driven MVCC tracing. | **No direct match found** for writable branch-local fallback/tombstone semantics plus cross-history minimum projection. |

## Final targeted searches

The final pass explicitly searched combinations of:

- branch-aware MVCC GC + overwrite/shadow/inherited parent key;
- tombstones/deletes stopping inherited branch fallback;
- cross-history / per-key retained projections;
- descendant-first branch GC / checkpoint / recovery ordering;
- branch snapshot pins and durable fork retention;
- clone/snapshot patents involving divergence, unlinking and GC;
- recent 2025–2026 branchable/versioned database/storage projects.

The search was checked against the local full-text corpus, including TardisDB, MatrixOne, HeliosDB, ForkBase, Decibel/ORPHEUSDB, precise MVCC GC papers, and COW snapshot work. Current project documentation and patent search were used only to attack the final claim, not to expand the paper into a general survey.

## Result

**No direct disclosure of the complete surviving A1 mechanism was found in the inspected sources.** This is an evidence-bounded literature conclusion, not a proof that no undiscovered prior art exists.

The closest obvious-combination attack remains:

> TardisDB-style branch visibility + HANA/Steam-style precise MVCC GC + ordinary COW/clone reachability.

A1 must therefore demonstrate that the nontrivial contribution is the **semantic cross-history construction and its durable publication**, not merely the fact that a child overwrite can release an ancestor object. The independent FlatExact baseline, observer-equivalence/minimality gates, low-shadow negative controls, and crash-safe descendant-first protocol are mandatory evidence against the “routine composition” criticism.

## Frozen claim wording for Holdout-A

A defensible claim entering Holdout-A is:

> For persistent writable branch trees with fixed ancestry boundaries, retained snapshots/historical observers, and MVCC value/tombstone semantics, shadow-aware projection propagates each retained `(observer, key)` requirement only until the first visible local state, yielding a sufficient and witness-minimal retained version set under the declared observer model. ChronicleDB realizes this projection with a descendant-first durable authority transition before reclaiming newly unnecessary ancestor history.

The paper may report the measured retained-space and physical-reclamation benefit **conditionally by workload regime**. It must retain the near-zero low-shadow results and must not claim universal space or maintenance-runtime improvement.

## Novelty disposition

**SURVIVES FINAL TARGETED NOVELTY VERIFICATION.**

No further broad literature expansion is required before Holdout-A. Reopen novelty review only if a newly discovered source directly addresses the same per-key cross-history semantic projection or its durable authority protocol.
