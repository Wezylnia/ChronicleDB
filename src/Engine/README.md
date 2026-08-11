# Engine

The Engine area contains orchestration-level contracts and mechanisms that operate above persistent codecs:

- `ChronicleDB.Transactions` owns transaction state and committed MVCC version management.
- `ChronicleDB.Recovery` interprets validated WAL/checkpoint history during open.
- `ChronicleDB.Maintenance` defines GC/compaction options and results.

These projects do not duplicate storage/WAL codecs or select concrete index implementations.
