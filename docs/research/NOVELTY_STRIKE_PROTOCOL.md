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

- **Narrow claim:** Writable ancestor histories ilerlemeye devam ederken her child edge'in sabit historical MVCC boundary'sinde kaldığı history tree için rebuildable, non-authoritative, logical per-key routing; value/tombstone/no-visible-version semantiği, compaction ve reopen altında güvenli fallback.
- **Claims we must not make:** Parent/lower-layer traversal'ın, resolved-layer memoization'ın, whiteout/tombstone shadowing'in, generation numbers'ın, Bloom/change summaries'nin veya binary lifting'in genel anlamda ilk kullanımı. Linux OverlayFS layered lookup + cached dentry result + whiteout semantiğiyle generic memoization claim'ini kapatır; Neon fixed `ancestor_lsn` ile database ancestor fallback semantiğini yakın biçimde kapsar.
- **Differentiating experiment:** Recursive fixed-boundary fallback; **simple per-key memoization (current P3B as baseline)**; eager materialization; branch-membership/change/range summaries; ancak bunlardan materially farklı bir MVCC-boundary-aware candidate bulunursa cold/warm, reopen/rebuild, tombstone, negative lookup, compaction, skew, invalidation bytes/cost ve reads-to-amortize Pareto matrix.
- **Kill condition:** Direct prior art aynı fixed historical MVCC-boundary route semantics'ini gösterir; veya simple memoization fair baseline olduktan sonra daha MVCC-specific candidate anlamlı Pareto farkı üretemez; veya fayda yalnız deep/high-reuse synthetic bölgede kalır.
- **Status:** `WEAKENED / HOLD` — problem BranchBench ile güçlü biçimde doğrulandı; OverlayFS/VHDX/qcow2 generic layered lookup/memoization sahipliğini, Neon fixed-boundary ancestor fallback sahipliğini, Oracle'ın 2001-priority clone-time work'u ise ancestor/current version ilerlerken historical clone validity'yi update-wide invalidation olmadan koruma fikrini kapatıyor. Current P3B engineering baseline olarak korunur; yeni A2 mekanizmasına şimdilik efor yok. Ayrıntı: `A2_NOVELTY_KILL.md`.

### Aday 8 — Erasure-consistent branching

- **Narrow claim:** Writable MVCC branch/snapshot/root observer contracts ile engine-controlled versions, branch bases, WAL generations ve checkpoints arasında exact erasure closure; snapshot-stability ile erasure authority çatıştığında explicit revocation ve crash-safe acknowledgement semantics.
- **Closest prior art / exact overlap:** Peterson et al. FAST 2005 doğrudan copy-on-write versioning file system içinde individual-version secure deletion, shared blocks ve off-site backup deletion mekanizmaları sunar. Boneh-Lipton USENIX Security 1996 backup kopyalarını cryptographic forgetting ile revoke eder. Lethe delete persistence latency ve physical purge'u LSM düzeyinde first-class problem yapar. Bu nedenle versioned storage + secure deletion, cryptographic erasure, shared-version deletion veya bounded persistent-delete latency tek başına novelty değildir.
- **Claims we must not make:** Versioned storage'da secure deletion'ın ilk örneği; shared historical representation'ları silmenin ilk çözümü; cryptographic erasure'ın, backup revocation'ın veya bounded physical delete latency'nin ilk kullanımı; engine dışı backup/device remanence garantisi.
- **Surviving mechanism / guarantee:** ChronicleDB'e özgü savunulabilir alan, fixed-base writable MVCC ancestry üzerinde hangi observer contract'larının eski değeri hâlâ legal olarak görünür tuttuğunu exact olarak çıkarmak; `RequestErasure` ile stability contract'ını koruyarak bloke etmek; yetkili `ForceErasure` için root revocation + engine-controlled WAL/checkpoint/version closure planı üretmek ve physical closure tamamlanmadan success acknowledge etmemektir.
- **Differentiating experiment:** `AnalyzeErasure`, `RequestErasure`, `ForceErasure`; blocker/revocation graph; tombstone-only, rewrite-all, version-chain secure-delete abstraction ve observer-exact closure karşılaştırması; nested branch/snapshot/WAL/checkpoint scenarios; crash-before/after authority publication.
- **Kill condition:** Branch observer contracts ve fixed ancestry, versioning-file-system secure deletion'dan farklı yeni safety/authority obligations üretmiyor; exact closure generic version-chain reachability'den ayrışmıyor; veya physical acknowledgement ancak mevcut secure-delete primitives'in doğrudan uygulanmasıyla açıklanabiliyor.
- **Verified primary sources (2026-08-11):** Peterson et al., *Secure Deletion for a Versioning File System*, FAST 2005, https://www.usenix.org/conference/fast-05/secure-deletion-versioning-file-system ; Boneh and Lipton, *A Revocable Backup System*, USENIX Security 1996, https://www.usenix.org/conference/6th-usenix-security-symposium/revocable-backup-system ; Sarkar et al., *Lethe: A Tunable Delete-Aware LSM Engine*, 2020 preprint/updated version, https://arxiv.org/abs/2006.04777 .
- **Status:** `WEAKENED` — generic versioned secure deletion novelty'si doğrudan prior art ile kapanmıştır; yalnız observer-contract revocation + multi-representation MVCC closure + crash-safe acknowledgement bileşimi araştırılmaya devam eder.

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

- **Narrow claim:** Independently durable writable-history tree içinde global catalog/ancestry/authority metadata validationını fail-closed tutarken, requested history'nin exact dependency closure ve history-local checkpoint/WAL replay work'ünü güvenli biçimde öne alan topology-aware recovery scheduling.
- **Closest prior art / exact overlap:** Lehman-Carey SIGMOD 1987 recovery'yi doğrudan "immediately needed" data için high-speed phase ve remaining database için background phase olarak ayırır. Sauer-Graefe-Härder Instant Restore, henüz restore edilmemiş data segments'i application demand üzerine restore edip read/write availability sağlar. Azure SQL Constant Time Recovery MVCC ile recovery availability'sini güçlü biçimde azaltır; PASV FAST 2022 optimized partial data recovery kullanır. Bu nedenle requested-first, on-demand, partial/background recovery veya fast availability genel iddiası novelty değildir.
- **Claims we must not make:** Needed-data-first recovery, background recovery, lazy/on-demand restore, partial recovery, instant availability veya MVCC-assisted fast recovery'nin ilk çözümü.
- **Surviving mechanism / guarantee:** Potansiyel fark, branch history'nin bağımsız WAL/checkpoint authority domain'leri ile shared branch catalog/root ancestry dependencies'ini aynı readiness contract altında ayırmak; global corruption validationını gevşetmeden yalnız expensive history-local reconstruction work'ünü topology/dependency closure'a göre schedule etmektir.
- **Differentiating experiment:** Frozen readiness state machine; recover-all, simple requested-first/per-object priority, dependency-closure-first ve topology-aware anchor baselines; identical corruption injection altında first-safe-history-ready, total work, sibling interference ve global fail-closed detection.
- **Kill condition:** Topology/authority-aware planner simple per-object/requested-data prioritizationdan ölçülebilir biçimde ayrışmıyor; aynı readiness contract altında anlamlı benefit yok; veya benefit global catalog/corruption validationını geciktirmeyi/gevşetmeyi gerektiriyor.
- **Verified primary sources (2026-08-11):** Lehman and Carey, *A Recovery Algorithm for a High-Performance Memory-Resident Database System*, SIGMOD 1987, https://research.ibm.com/publications/a-recovery-algorithm-for-a-high-performance-memory-resident-database-system ; Antonopoulos et al., *Constant Time Recovery in Azure SQL Database*, SIGMOD 2019, https://www.microsoft.com/en-us/research/publication/constant-time-recovery-in-azure-sql-database/ ; Sauer, Graefe and Härder, *Instant restore after a media failure*, https://arxiv.org/abs/1702.08042 (extended journal version: Information Systems 82, 2019); Huang et al., *Removing Double-Logging with Passive Data Persistence in LSM-tree based Relational Databases*, FAST 2022, https://www.usenix.org/conference/fast22/presentation/huang .
- **Status:** `WEAKENED` — generic selective/needed-first recovery novelty'si güçlü prior art ile kapanmıştır; yalnız multi-history authority/topology scheduling + unchanged global fail-closed semantics bileşimi araştırılmaya devam eder.

## R0 teslimi

R0 sonunda her card `Status` alır; Aday 8 ve 17 için primary-source listesi ve exact-overlap notu eklenir. `BLOCKED-BY-NOVELTY` card'ı katalogdan silinmez; yalnız deep implementation ve publication claim'inin durdurulmasını ifade eder.
