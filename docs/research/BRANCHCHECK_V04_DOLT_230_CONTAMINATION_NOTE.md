# BranchCheck v0.4 — Why Dolt 2.3.0 is not the fair-budget headline

The Dolt fair-budget workflow now runs 2.2.3 and 2.3.0 in separate CI jobs so process/database locks cannot leak across versions.

The isolated 2.3.0 campaign completes, but its three observed continuation failures are **not one homogeneous historical allocator family**:

- `Pull` rejects the generated insert with a duplicate primary-key terminal;
- `FetchOnly` rejects with `context canceled`;
- `FetchMerge` rejects with `context canceled`.

The latter two terminals overlap the independently isolated dynamic-clone request-context race discovered during v0.4. Therefore the 2.3.0 budget curve is root-cause-contaminated: treating all three failures as evidence for one history-import / allocator-refresh class would overstate the result.

For the paper:

- use **Dolt 2.2.3** as the frozen second-backend fair-budget result;
- keep **Dolt 2.3.0** as exploratory/current context only;
- analyze the dynamic-clone `context canceled` race as a separate root-cause family;
- never combine duplicate-PK and request-context failures into one issue count or one homogeneous search-success statistic.

The isolated 2.2.3 job has an explicit CI assertion for the frozen shape:

- `NoOp` continuation Pass;
- `FetchOnly`, `Pull`, `FetchMerge` continuation Fail;
- all three relevant history operations B4 Pass;
- budget-1 generic detection 75%;
- budget-1 guided detection 100%;
- budget 2+ both 100%.
