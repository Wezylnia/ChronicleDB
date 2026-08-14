# False-Positive and Trigger/Oracle Analysis

## Dolt sequence-authority false positive

An early oracle model treated the next generated identifier as `visible MAX(primary-key) + 1`. That model is not a universal Dolt contract: sequence authority can incorporate branch and remote-reference state that is not visible in ordinary rows. The model therefore produced a candidate violation without proving a backend defect.

The corrected rule is:

> A cross-system oracle must be capability- and contract-aware; visible-state equality is not sufficient to infer allocator authority.

The Dolt contamination and causal-control notes remain separate from confirmed findings. No current-main Dolt candidate is called novel without independent reproduction and upstream confirmation.

## 2×2 trigger/oracle matrix

The evaluation separates “who constructs the witness?” from “who diagnoses a supplied failure?”

| | Generic oracle catches supplied failure | Generic oracle misses supplied failure |
|---|---|---|
| BranchCheck constructs the trace | **A:** trigger advantage and diagnosis overlap; report both separately | **B:** semantic guidance plus relation-specific diagnosis |
| BranchCheck misses the trace | **C:** generic trigger/diagnosis is sufficient; negative result | **D:** unresolved; do not claim coverage |

Current local evidence occupies these cells:

| Evidence | Trigger interpretation | Oracle interpretation | Cell / use |
|---|---|---|---|
| MatrixOne AUTO_INCREMENT transcript | exact witness supplied; no trigger claim | B2 detects terminal mismatch | negative control for exclusivity |
| MatrixOne historical identity transcript | controlled candidate ordering can prioritize identity class | B4 detects supplied command failure; BC names boundary identity | semantic-obligation example |
| SlateDB transcript | witness supplied | B5 detects observer failure | paired observer example |
| Dolt historical transcript | lifecycle witness supplied | B4 detects supplied failure | closure example |
| Local fair-budget calibration | guided ordering is intentionally target-class aware | local semantic predicate only | harness calibration, not paper evidence |
| Local unseeded pilot | uniform issue-ID-free ordering | local semantic-class predicate | protocol validation, not external evidence |

## Required reporting rule

Every future external result must report four fields independently:

1. whether the candidate grammar constructed the witness;
2. whether a generic baseline constructed it under the same budget;
3. whether the supplied trace was detected by each oracle;
4. whether BranchCheck provided a unique semantic explanation.

Do not collapse these into one “found/not found” number. Retain false positives, oracle ambiguity, backend unavailability, and tool failures in the result ledger.
