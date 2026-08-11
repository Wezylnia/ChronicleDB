# v1.1 Novelty Strike Protocol

Bu belge, ChronicleDB v1.1 aday mekanizmaları kodlanmadan önce novelty iddialarını dondurur. Deneysel büyüklük novelty kanıtı değildir; her adayın dar iddiası, en yakın prior art'ı ve ayırt edici deney ayrı kaydedilir.

Ana literatür haritası: `private-docs/saucis-post-v1-literature-and-topic-candidates.md`.

## Novelty card sözleşmesi

Her aday için aşağıdaki alanlar doldurulmadan candidate-specific mechanism baseline ölçümüne giremez:

```text
CandidateId
NarrowClaim
ClosestPriorArt
ExactOverlap
ClaimsWeMustNotMake
SurvivingMechanism
SurvivingGuarantee
DifferentiatingExperiment
KillCondition
SearchDateAndCutoff
VerifiedPrimarySources
OpenQuestions
Status
```

`Status` yalnız şu değerlerden biri olabilir:

```text
UNATTACKED
ATTACKED
WEAKENED
BLOCKED-BY-NOVELTY
INCONCLUSIVE
```

Bir card değiştiğinde eski sürüm korunur; değişiklik gerekçesi, tarih ve yeni kaynaklar eklenir. “İlk”, “novel”, “general”, “sound” ve “secure” gibi geniş iddialar, card'daki exact overlap incelemesi tamamlanmadan kullanılmaz.

## Candidate cards — v1.1 başlangıç sürümü

### Aday 1 — Observer-exact MVCC retention and crash-safe reclamation

- **Narrow claim:** History roots ve observer boundaries için exact logical marginal retention; recovery-authority transition sonrasında ölçülmüş physical realization.
- **Claims we must not make:** Snapshot-set what-if veya reclaimable-space hesabının ilk örneği; coarse block accounting'in genel fikrinin ilk kullanımı.
- **Differentiating experiment:** `MarginalDebt(S | C)` reference oracle, root overlap, per-key observer visibility, checkpoint/WAL authority ve allocated-block physical measurement.
- **Kill condition:** Exact logical result coarse/root baselines'tan ayrışmıyor veya bağımsız reference oracle ile doğrulanamıyor.
- **Status:** `ATTACKED` — NetApp/ZFS/FlexVol prior art'ı nedeniyle claim daraltıldı.

### Aday 2 — Online ancestry acceleration

- **Narrow claim:** Fixed parent-boundary branch resolution için rebuildable, false-negative-free, stable ancestry routing ile read amplification/P99–memory–write Pareto iyileşmesi.
- **Claims we must not make:** Generation numbers, Bloom summaries, memoization veya binary lifting'in genel anlamda ilk kullanımı.
- **Differentiating experiment:** Recursive, memoized ve stable routing baselines; cold/warm, reopen/rebuild, tombstone, negative lookup, compaction ve skew matrix.
- **Kill condition:** Depth/topology gerçek bottleneck üretmiyor veya P3-B baseline'lara karşı Pareto üstünlük göstermiyor.
- **Status:** `ATTACKED` — Git commit-graph, persistent structures, Decibel/ForkBase mekanizmalarıyla daraltılmalı.

### Aday 8 — Erasure-consistent branching

- **Narrow claim:** Branch/snapshot/root observer contracts ile engine-controlled representations arasında exact erasure closure ve explicit revocation semantics.
- **Claims we must not make:** Genel secure deletion, cryptographic erasure veya engine dışı backup/device remanence garantisi.
- **Differentiating experiment:** `AnalyzeErasure`, `RequestErasure`, `ForceErasure`; blocker/revocation graph; tombstone, rewrite-all ve closure-aware plan karşılaştırması.
- **Kill condition:** Branch topology observer-contract conflict üretmiyor veya closure generic secure-deletion probleminden ayırt edilemiyor.
- **Status:** `UNATTACKED` — secure deletion, versioned filesystem, snapshot/backup erasure ve cryptographic erasure taraması R0'da tamamlanacak.

### Aday 9 — History-domain POR

- **Narrow claim:** Fixed ancestry, multiple history-local authority domains ve shared catalog/root dependencies için declared failure model altında property-relevant canonical observation-trace preserving reduction.
- **Claims we must not make:** Crash testing için genel POR'un veya multi-domain pruning'in ilk örneği.
- **Differentiating experiment:** Declared persistence model, exhaustive oracle, explicit independence relation, canonical observation traces ve adapted prior-art baselines.
- **Kill condition:** Tek property-relevant trace mismatch, zorunlu mutant miss veya reduction'ın strong baselines karşısında anlamsız kalması.
- **Status:** `ATTACKED` — Jaaru/PACE/Silhouette/Pathfinder nedeniyle soundness ve failure-model sınırı şart.

### Aday 10 — Tree-compositional recovery verification

- **Narrow claim:** ChronicleDB history ancestry, shared catalog/lifecycle ve authority publication invariants için generic composition'dan ayrılan proof obligations.
- **Claims we must not make:** Genel compositional crash recovery veya local proofs imply global safety.
- **Differentiating experiment:** Minimal Dafny proof-feasibility spike, explicit refinement mapping, trusted boundary ve executable trace checker.
- **Kill condition:** Tüm obligations Argosy/crash-aware compositionality'nin doğrudan instantiation'ı çıkıyor.
- **Status:** `ATTACKED` — Argosy, DaisyNFS ve crash-aware linearizability nedeniyle yüksek riskli.

### Aday 17 — History-selective recovery

- **Narrow claim:** Global metadata/ancestry validationı fail-closed tutarken requested history'nin dependency closure ve local replay'ini güvenli biçimde öne alan recovery scheduling.
- **Claims we must not make:** Genel lazy recovery, instant restore veya corruption isolation'ın ilk çözümü.
- **Differentiating experiment:** Frozen readiness state machine; recover-all, requested-first ve dependency-closure-first baselines; requested readiness, safety ve corruption semantics.
- **Kill condition:** Aynı readiness contract altında ölçülebilir readiness farkı yok veya global fail-closed/corruption guarantees korunamıyor.
- **Status:** `UNATTACKED` — partial availability, recovery scheduling, selective WAL replay, lazy recovery ve instant restore taraması R0'da tamamlanacak.

## R0 teslimi

R0 sonunda her card `Status` alır; Aday 8 ve 17 için primary-source listesi ve exact-overlap notu eklenir. `BLOCKED-BY-NOVELTY` card'ı katalogdan silinmez; yalnız deep implementation ve publication claim'inin durdurulmasını ifade eder.
