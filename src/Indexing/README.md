# Indexing

`ChronicleDB.Indexing.Abstractions` defines the stable collision-safe version-index contract. `ChronicleDB.Indexing.Baseline` provides the managed v1.0 implementation.

The public composition root selects the implementation. Transaction and recovery semantics depend only on the abstraction so later latch-free research can be evaluated against the same logical contract.
