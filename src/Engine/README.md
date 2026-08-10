# Engine orchestration

Transactions coordinates foreground commit behavior, Recovery reconstructs durable state during open, and Maintenance plans/reclaims/compacts obsolete physical state. These projects orchestrate owned modules; they do not duplicate codecs or concrete index algorithms.
