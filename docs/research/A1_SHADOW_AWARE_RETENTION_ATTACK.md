# A1 Novelty/Falsification Attack — Shadow-Aware Cross-History Retention

Status date: 2026-08-12

## Pilot decision

**GO at the falsification gate. Current research score: 96/100. Not yet a publication result.**

The original broad A1 framing is retired. Counterfactual snapshot/root deletion, exact MVCC pruning, branch-aware GC, branch-base history protection, and logical-vs-physical reclamation are all prior-art-constrained ideas and must not be claimed as novel by themselves.

The surviving hypothesis is narrower:

> In a writable persistent MVCC history tree, descendant observer requirements can be propagated to ancestors **per key**. A local value or tombstone stops fallback for that key once all retained descendant observers are at/after the shadow, while an inherited key continues to require the ancestor predecessor. The resulting cross-history projection can therefore be smaller than a strong per-history exact baseline without changing any retained observer result.

The research implementation remains opt-in. Default ChronicleDB GC semantics are unchanged.

## Strong baseline

The candidate is compared against the existing per-history exact projection, not only against a coarse global or branch-level horizon. The baseline already retains versions at/above each history floor and the visible predecessor required by each persistent/active boundary. BranchBase roots conservatively protect a parent predecessor for every key.

The candidate changes only BranchBase treatment: branch bases become ancestry edges. A descendant requirement reaches the parent only when the key has no visible local value or tombstone at the retained descendant boundary.

## Correctness model

For every retained observer `o` and key `k`, resolution follows normal branch read semantics:

```text
Resolve(history, boundary, key):
    visible local VALUE      -> retain it; stop
    visible local TOMBSTONE  -> retain it; stop
    no local visible version -> Resolve(parent, fixed-parent-boundary, key)
    no parent                -> missing
```

The candidate projection is the union of versions reached by legal retained observers plus the ordinary per-history time-travel requirements.

The falsification gates are:

1. candidate retained set is a subset of the strong baseline;
2. every legal observer/key read is identical before and after projection;
3. every retained candidate version has at least one observer witness (minimality under the enumerated observer model);
4. pre-shadow persistent snapshots and active historical views prevent unsafe release;
5. physical publication is descendant-first when an ancestor release depends on a descendant floor advance;
6. crash/reopen returns a legal equivalent observer state.

## Semantic attack results

The reference oracle covers overwrite, tombstone, pre/post-shadow persistent snapshots, pre/post-shadow active historical views, nested ancestry, sibling sharing, and missing-key fallback.

- targeted shadow-aware oracle tests: PASS;
- randomized semantic forest fuzz: 400 random forests PASS;
- randomized forests used 2–6 histories, four keys, random floors, tombstones, persistent roots and active boundaries;
- all candidate-subset, observer-equivalence and witness-minimality gates remained satisfied;
- real-engine single-branch matrix: 30/30 PASS;
- staggered fanout matrix: 62/62 PASS;
- mixed random-shadow matrix: 12/12 PASS;
- differential observer checks in the staggered campaign: 3,520 checks, 0 mismatches;
- pre-shadow snapshot/active-view controls reduce shadow release to zero as required.

## Effect-size attack

The effect is **not universal** and must not be presented that way.

Representative logical results with 4 KiB values:

| Workload | Shadow-aware reclamation ratio |
| --- | ---: |
| one branch, 100% overwrite | 1.50x |
| one branch, 100% tombstone | 2.00x |
| 8 staggered branches, 100% overwrite | 1.89x |
| 8 staggered branches, 100% tombstone | 9.00x |
| nested overwrite chain, depth 16 | ~1.94x |
| 8 branches, 25% overwrite shadow | ~1.22x |
| 8 branches, 10% overwrite shadow | ~1.09x |
| 8 branches, 1% overwrite shadow | ~1.009x |

The useful regime is therefore **multiple long-lived retained branch bases plus substantial key shadowing**. Low-shadow workloads are explicit negative controls.

### Closed-form effect bound for the controlled workload

For the equal-value-size staggered-branch workload, let `B` be branch count, `s` the shadowed-key fraction, `t` the fraction of shadows that are tombstones, and `M` the current Main payload. The experiment oracle predicts:

```text
baseline payload  = M * (1 + B + B*s*(1-t))
released parent   = M * B*s
candidate payload = baseline - released parent
SAR               = baseline / candidate
```

Two useful bounds follow. With full overwrite shadow (`s=1,t=0`), `SAR=(1+2B)/(1+B)` and therefore approaches but never exceeds `2x`. With full tombstone shadow (`s=1,t=1`), `SAR=B+1`. The measured 1.89x overwrite and 9.0x tombstone results at eight branches match these bounds exactly. This model is an experiment oracle under controlled assumptions, not a production sizing formula.

## Physical realization

Paired physical experiments start from the same database image and apply current exact GC to one copy and descendant-first shadow-aware GC to the other, followed by compaction and restart observer comparison.

For 8 staggered branches, 100% overwrite, 4 KiB values:

| Keys | Logical payload released | Final allocated reduction | Logical SAR |
| ---: | ---: | ---: | ---: |
| 64 | 2 MiB | ~2.04 MiB | 1.89x |
| 256 | 8 MiB | ~8.14 MiB | 1.89x |
| 1024 | 32 MiB | ~32.56 MiB | 1.89x |

The 100% tombstone variant produced the same physical reduction while the logical payload ratio reached 9.0x for eight staggered branch bases.

The 64 -> 256 -> 1024 physical series scales approximately linearly with released payload. A 2048-key run completed baseline/candidate GC and compaction but exceeded the external command window before final restart/result serialization; it is **not counted as a completed result**.

All completed paired physical cases retained equivalent Main/branch current observer state after restart.

## Crash-safety attack

The publication protocol is descendant-first: a descendant authority/floor that justifies removing an ancestor predecessor must become durable before the ancestor projection is published.

In-process storage-fault tests cover checkpoint header/record/output-flush and WAL-reset boundaries; all configured shadow-GC recovery cases pass.

The separate-process crash harness now also uses `Environment.FailFast` for five shadow-aware authority-transition points:

1. child checkpoint output flush;
2. parent checkpoint record write;
3. parent checkpoint output flush;
4. parent before WAL reset;
5. parent after WAL reset.

One full crash-harness iteration returned RC=0; all five new process-death scenarios reopened with correct Main and branch values, and existing crash scenarios remained passing.

## Projection-cost attack

A disk-independent scale mode measures the complete verified research projection (index construction + core projection + observer equivalence/minimality verification).

With 8 branches, 100% overwrite:

| Keys | Logical versions | Verified projection median | Transient thread allocation |
| ---: | ---: | ---: | ---: |
| 64 | 1,088 | ~4.8 ms | ~2.1 MiB |
| 256 | 4,352 | ~17 ms | ~8.5 MiB |
| 1,024 | 17,408 | ~57–70 ms | ~33.5 MiB |
| 4,096 | 69,632 | ~210–232 ms | ~137 MiB |
| 8,192 | 139,264 | ~499 ms | ~170 MiB after traversal optimization |
| 16,384 | 278,528 | ~1.05 s | ~344 MiB |
| 32,768 | 557,056 | ~2.41 s | ~695 MiB |

At 32,768 keys the process peak RSS was about 450 MiB. The observed curve is approximately linear over the tested range, not explosive, but the current research implementation is allocation-heavy and is not presented as a production-optimized implementation.

The traversal optimization (no observer×key queue materialization, binary-search local resolution, no per-read ancestry HashSet) improved the 8,192-key case from ~684 ms to ~499 ms and reduced transient allocation from ~247 MiB to ~170 MiB while preserving all semantic gates.

For a 4,096-key / 8-branch representative run, the optimized verified projection was about 216 ms, with about 165 ms in core projection and about 10 ms in exhaustive observer verification. These timings are research diagnostics, not publication holdout measurements.

At a fixed 4,096 keys and 100% overwrite shadow, the current optimized branch-count curve is:

| Branches | Logical versions | Verified projection median | Core projection median | Transient thread allocation | Logical SAR |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 8 | 69,632 | ~216 ms | ~165 ms | ~99 MiB | 1.89x |
| 16 | 135,168 | ~350 ms | ~287 ms | ~180 MiB | 1.94x |
| 32 | 266,240 | ~860 ms | ~636 ms | ~378 MiB | 1.97x |
| 64 | 528,384 | ~1.86 s | ~1.57 s | ~837 MiB | 1.98x |

The curve is deliberately adversarial and confirms that the current prototype belongs in the maintenance/checkpoint path rather than a foreground read path. Runtime growth is manageable over the tested range, but transient allocation remains a real engineering limitation and must be reported rather than hidden.

## Maintenance-timing caveat

End-to-end physical runs often show lower GC+compaction time for the candidate. For example, one 1,024-key / 8-branch / 100%-overwrite run measured roughly 4.93 s for baseline maintenance vs 2.24 s for the shadow-aware path.

This is **not yet a defensible causal performance claim** because the baseline GC and research GC execute different implementation paths. The warning is supported by the 1%-shadow negative control: only ~0.32 MiB was physically saved, yet repeated runs still showed about 1.25x median maintenance speedup. Therefore maintenance speedup must be treated as implementation-path/confounding evidence until an apples-to-apples implementation or controlled breakdown is available.

The paper contribution should be based on retained-set reduction, observer correctness, physical realization and crash-safe authority ordering—not on the current maintenance speedup number.

## Prior-art boundary

The following are constraints, not novelty claims:

- HANA/Steam-style interval MVCC GC: precise visibility-aware version pruning;
- TardisDB: branch-aware tuple/version visibility and branch-lineage semantics;
- HeliosDB technical note: hierarchical branch-aware MVCC GC using branch/snapshot horizons;
- MatrixOne and related systems: snapshot/branch-protected GC;
- NetApp/ZFS/Btrfs-style work: snapshot/set-level reclaimability and physical accounting concepts.

The surviving claim should therefore be stated as the **combination and algorithmic treatment of per-key descendant shadowing across persistent writable history ancestry**, plus a crash-safe descendant-first authority transition and measured physical realization.

The final targeted novelty-kill pass on 2026-08-12 did not find a direct source that computes the same minimum per-key cross-history projection. TardisDB provides branch bitmap/version-chain visibility and proposes collecting versions that are no longer contained in any branch; HeliosDB propagates descendant branch and snapshot requirements through a scalar per-branch LSN horizon. Those are the strongest composition threats found so far, but neither inspected algorithm expresses the ChronicleDB candidate's key-specific rule that a retained descendant value/tombstone stops the corresponding ancestor requirement while unshadowed keys continue to propagate. This is an evidence-bounded literature conclusion, not a claim that no such work can exist.

## Current decision and kill conditions

The new A1 hypothesis survived the first implementation attack and is stronger than the original retention-debt framing.

It should be demoted or abandoned if any of the following occurs:

1. a direct prior-art algorithm is found that already computes the same per-key shadow-aware cross-history minimum retained projection under persistent branch/snapshot observers;
2. realistic workload families show only ~1.0–1.1x retained-set improvement outside contrived high-shadow cases;
3. a legal retained observer is found whose result changes under the candidate projection;
4. descendant-first crash publication cannot be made safe under the declared persistence failure model;
5. publication-scale projection overhead becomes prohibitive after reasonable implementation cleanup.

## Next publication work

1. Rebase/apply the A1 research commits onto the current GitHub `main` and rerun the complete gate there; the current local checkout was older, although the production retention/GC blobs used by the hypothesis were verified byte-identical to the then-current main.
2. Perform one final targeted novelty-kill search centered on Tardis/branch visibility + precise MVCC GC composition and any 2025–2026 shadow-aware branch reclamation work.
3. Define two realistic workload families (not only synthetic fanout): branch-mutating analytical/agent workflow and long-lived development/test branches with skewed key updates.
4. Freeze the strong baseline and candidate configuration.
5. Preregister and run independent-process Pilot-A, then sealed Holdout-A. Do not tune after opening holdout data.

