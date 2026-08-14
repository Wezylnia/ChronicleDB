# Local Capability-Budget Calibration

This campaign validates the fair-budget machinery before external systems are provisioned. It is deliberately labeled **calibration**, not paper evidence: the target semantic class is supplied to the guided scheduler and the terminal violation is represented by candidate semantic metadata rather than a live database oracle.

## Frozen protocol

- Candidate set: `CapabilityCandidateGrammar.Generate(profile)`.
- Uniform ordering: seeded Fisher–Yates permutation.
- Guided ordering: same candidate set, target semantic class first, seeded tie-breaks.
- Seeds: `1, 7, 13, 29, 61, 127, 251, 509`.
- Budget: every prefix from 1 through the complete candidate count.
- Detection: a prefix contains a candidate whose semantic class intersects the target class.
- Issue IDs and historical reproducer names: not used.
- External evidence: false; this is a local protocol/harness calibration.

## Current calibration runs

The executable command is:

```text
dotnet run -c Release --no-build --project tools/ChronicleDB.BranchCheck -- local-budget
```

The machine-readable output is [`local-capability-budget.json`](../../artifacts/baseline/local-capability-budget.json). It contains four capability profiles (identity, allocator, observer, recovery), candidate-set fingerprints, complete budget curves, and both detection counts/rates.

The guided scheduler reaches a target-class candidate at budget 1 for all four calibration profiles, while uniform ordering reaches it only according to the seeded permutation. This is expected protocol behavior and must not be reported as an external speedup. The external fair-budget experiments must replace the synthetic target predicate with a backend execution/oracle while retaining the same candidate-set and seed discipline.
