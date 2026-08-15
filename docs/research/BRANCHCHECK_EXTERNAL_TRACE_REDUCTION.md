# External Trace Reduction Freeze

This document records the final reduction pass over the expanded external candidate observations. The reducer operates on the exported semantic candidate frames, not on a reconstructed backend log. It repeatedly removes candidates while requiring the same relation id, failure polarity, and generic-baseline polarity. Therefore the reduction is a semantic witness reduction; it is not a claim that the backend's internal physical trace had the same number of events.

| Evidence | Original candidate frames | Reduced frames | Preserved signature | Generic baseline | Reduction runtime |
|---|---:|---:|---|---|---|
| MatrixOne v2 identity | 10 | 1 (`RecreateSourceSameName`) | `BC.temporal-boundary=Fail` | `Pass` | not measured (offline artifact reduction) |
| Dolt 2.2.3 expanded | 10 | 1 (`FetchOnly`) | `BC.continuation-state=Fail` | `Detected` | not measured (offline artifact reduction) |
| Dolt 2.3.0 control-preserving view | 10 | 1 (`FetchOnly`) | `BC.continuation-state=Fail` | `Detected` | not measured (offline artifact reduction) |
| SlateDB 0.14.1 expanded | 8 | 1 (`CloneDbReader`) | `BC.observer-dependency=Fail` | dependency-read oracle | not measured (offline artifact reduction) |
| SlateDB fix `6a131a9e` | 8 | 0 | no failure (negative control) | all readable | not applicable |

The original candidate arrays, evidence details, version identities, and fingerprints remain immutable in the external archives. The single-frame rows are sufficient for the relation signature but do not replace the full fair-search denominator; all budget curves continue to use the complete frozen candidate sets.

## Reduction rule

1. Keep the candidate set and backend/version identity fixed.
2. Remove one candidate at a time in deterministic source order.
3. Accept a removal only when the selected relation status, relation id, generic-baseline status, and semantic-class label remain unchanged.
4. Stop at a one-candidate witness or at the empty negative-control trace.

No recipe was selected during the fair-search execution using this reduction. Reduction was applied only after the complete candidate observations and budget results were frozen.
