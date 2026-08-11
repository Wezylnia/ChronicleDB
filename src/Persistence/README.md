# Persistence

Persistence owns durable byte protocols and physical I/O. `ChronicleDB.Storage` manages database files, pages, metadata journals, snapshots, branches, and retained-history checkpoints. `ChronicleDB.Wal` owns WAL framing, append/flush behavior, and structural validation.

The two remain separate because transaction-log authority and physical storage representation have different ordering, recovery, and corruption rules.
