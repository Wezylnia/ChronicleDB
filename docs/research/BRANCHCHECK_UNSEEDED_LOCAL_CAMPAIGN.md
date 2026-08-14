# Unseeded Local Campaign Protocol

This is the local pilot for the plan's unseeded-campaign gate. “Unseeded” here means the candidate grammar and target semantic families were frozen without using historical issue IDs or reproducer traces. The run still uses a predeclared reproducibility seed ledger; it is not a claim of entropy or external-system discovery.

## Frozen protocol

- Grammar: `capability-grammar-v1` from `CapabilityCandidateGrammar`.
- Profiles: historical identity, allocator continuation, observer dependency, recovery closure.
- Ordering: uniform seeded permutation; no BranchCheck target ordering is used.
- Seeds: 32 fixed values in `UnseededLocalCampaign.FrozenSeeds`.
- Trace budget: 8 candidates per run.
- Oracle: semantic-class membership in the frozen local grammar.
- Outcomes retained: `known-failure` and `no-failure`; infrastructure failures would also be retained if a backend were involved.
- External evidence: false.

## Result

The command:

```text
dotnet run -c Release --no-build --project tools/ChronicleDB.BranchCheck -- unseeded-local
```

produced 128 runs (4 profiles × 32 seeds): 114 local known-failure classifications and 14 no-failure classifications. The complete run ledger, candidate-set fingerprints, first matching positions, and outcome counts are in [`unseeded-local.json`](../../artifacts/baseline/unseeded-local.json).

This result validates the predeclared orchestration and outcome accounting only. It must not be reported as a DBMS bug-discovery rate. The same protocol becomes an external experiment when the semantic candidate predicate is replaced by a live backend execution and the full B0–B5/BC oracle set.
