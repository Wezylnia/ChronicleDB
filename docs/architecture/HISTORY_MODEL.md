# History model

ChronicleDB represents database history as a rooted tree of independently writable `HistoryId` domains. Main is the root history. Every branch has exactly one immutable parent boundary `(ParentHistoryId, ParentBaseSequence)` and an independent local commit-sequence namespace.

A logical branch state at local sequence `S` is the union of:

1. the newest branch-local version of each key visible at `S`; and
2. for keys with no visible local version, the immutable parent state at the branch base.

A local tombstone is a visible local version and therefore blocks parent fallback.

Persistent snapshots are read-only roots into one history domain. Branch bases are roots owned by a child history that protect a boundary in the parent history. Active transactions and open historical handles are temporary process-local observers.

v0.9 retention treats these roots as reachability requirements. Generic time travel may have a newer floor, while isolated older snapshots and branch bases remain reconstructable through explicit retained versions.
