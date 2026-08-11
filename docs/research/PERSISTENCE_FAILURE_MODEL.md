# ChronicleDB v1.1 Declared Persistence Failure Model

Bu model Aday 9 bounded crash exploration ve ilgili recovery trace iddialarının kapsamını tanımlar. Soundness yalnız burada yazılı varsayımlar altında geçerlidir.

## Storage operations

Modelde gözlemlenebilir operasyonlar:

- normal write ve partial/torn write;
- WAL append, record framing ve complete-record checksum;
- `Flush`/`fsync` stable-storage barrier;
- file replace/rename;
- directory durability;
- checkpoint record publication;
- WAL reset/rollover;
- stale generation cleanup;
- process crash;
- crash during recovery.

Her event `LogicalEventId`, resource set, history/resource identity, durability phase, authority generation ve dependency event ids ile trace'e girer.

## Semantic assumptions

1. Complete framed record checksum failure corruption'dır; crash tail yalnız framing'in incomplete olduğu kanıtlanırsa truncation adayıdır.
2. Acknowledged durable commit, desteklenen process crash sonrasında authoritative recovery state'inde kaybolamaz.
3. WAL reset, eşdeğer retained-history checkpoint stable storage'a geçmeden gerçekleşemez.
4. History-local WAL record'ları branch/history identity envelope'u ile bağlanır; başka history'ye replay edilemez.
5. Parent/base/catalog/root/lifecycle dependency'leri yalnız dependency relation tarafından bağımsız sayılmayabilir.
6. Compaction physical representation'dır; authoritative checkpoint + WAL ile yeniden kurulabilen derived state, publication validation'ı geçmeden kabul edilmez.
7. Crash sırasında hangi writes/flushes/renames'ın durable olduğu model event'leriyle belirtilir; model dışı filesystem/hardware davranışı soundness kapsamı değildir.

## Failure plan universe

Bir bounded campaign aşağıdaki plan uzayını manifest'te açıklar:

```text
operation trace prefix
durability barrier boundaries
authority publication boundaries
WAL reset/cleanup boundaries
crash during recovery boundaries
```

Random, phase-only veya resource-only reducer bu declared universe'ün alt kümesi olarak etiketlenir; exhaustive oracle ile aynı kapsamdaymış gibi sunulmaz.

## Observation equivalence

POR doğruluğu low-level I/O equality istemez. Irrelevant implementation events normalize edilebilir; fakat aşağıdaki property-relevant trace olayları korunmalıdır:

- authority accepted/published;
- history validated/ready/unavailable/corrupt;
- recovery started/completed;
- corruption detected;
- root/lifecycle transition;
- safety predicate sonucu;
- recovered observer state.

Tek terminal key/value state equality yeterli değildir. `ObsTraces(reduced) ≡ ObsTraces(exhaustive)` yalnız bu canonical observation projection için kullanılır.
