# Dolt Robustness, Timing, and Causal Sweep

The final workflow is `.github/workflows/branchcheck-dolt-robustness.yml`. Its matrix is explicit: five targets (Dolt 2.2.3, 2.3.0, pinned current main unpatched, `context.Background()`, and `context.WithoutCancel`) crossed with seven continuation delays (0, 1, 5, 10, 50, 100, 500 ms). Each cell executes 100 fresh repetitions.

Every repetition preserves:

- continuation outcome and relation status;
- generated id and expected/actual continuation tokens;
- server process health after the continuation;
- elapsed continuation timing;
- harness failures, including stderr, rather than dropping them.

The workflow intentionally reports timing descriptively (min/median/max per cell). It does not turn the 100-run cell counts into a universal failure probability. The three causal arms differ only in the preregistered context-lifetime line; the database schema, operation budget, delay sweep, and source commit remain fixed.

The imported archive is validated as 35 unique cells and 3,500 repetitions by the `DoltRobustness` evidence kind. The workflow run and archive digest are recorded in `artifacts/external-frozen/manifest.json` after completion.

## Observed sweep summary

| Target | Relation failures / 700 reported runs | Harness failures | Interpretation |
|---|---:|---:|---|
| Dolt 2.2.3 | 0 | 19 | release negative control; one delay cell lost 19 reports to harness failures |
| Dolt 2.3.0 | 221 | 19 | stochastic/version-specific continuation regression; not a universal rate |
| current main, unpatched | 300 | 12 | race-sensitive source reproduction across delays |
| current main + `context.Background()` | 0 | 0 | causal rescue control |
| current main + `context.WithoutCancel` | 0 | 26 | causal rescue with retained harness failures |

The per-delay summaries and raw repetitions are the authoritative source. Counts above are descriptive totals over the seven delay cells; they are not confidence intervals or production failure probabilities.
