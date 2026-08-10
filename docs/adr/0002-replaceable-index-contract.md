# ADR 0002: Replaceable index contract

- Status: Accepted
- Date: 2026-08-10

## Context

The plans require an understandable managed baseline index and a later Bw-tree-inspired latch-free implementation. Transaction, recovery, and maintenance semantics must remain identical across both.

## Decision

Place the stable logical index seam in `ChronicleDB.Indexing.Abstractions`. Place the initial managed implementation in `ChronicleDB.Indexing.Baseline`. Transaction, recovery, and maintenance assemblies reference only the abstraction. The public facade selects the concrete implementation.

The contract expresses logical keys and version heads, not implementation nodes, locks, mapping-table slots, delta records, epochs, or pointers.

## Consequences

- Baseline and optimized implementations can run against identical workloads.
- Physical implementation details cannot leak into transaction semantics.
- The abstraction must remain deliberately small; convenience methods are added only when both implementations can honor identical semantics.
