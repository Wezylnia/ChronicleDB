# Historical retention

ChronicleDB v1.0 retains the v0.9 design that separates a history's generic time-travel range from explicit roots that protect isolated older states. This distinction prevents one ancient branch or snapshot from forcing the entire database to retain every unrelated intermediate version.

## Generic history floor

Each `HistoryId` has a monotonic generic retention floor. Ordinary time-travel opens are supported for boundaries from that floor through the current committed sequence. GC may advance the floor but never move it backwards.

## Explicit persistent roots

Persistent snapshots and branch bases remain valid even when their boundary is older than the generic floor. A root protects the newest version of each key visible at its exact boundary rather than implicitly protecting every version newer than that root.

A `PersistentSnapshot` root protects its own history. A `BranchBase` root is owned by the child history but protects the parent history at `ParentBaseSequence`.

## Process-local observers

Active transactions, open historical views, and open snapshot handles register exact `(HistoryId, CommitSequence)` boundaries. GC captures those boundaries while holding the history lifecycle gate. Opening a new historical handle is serialized with floor advancement so a successful handle can never race with reclamation of the state it just opened.

Deleting a named persistent snapshot removes its persistent root but does not invalidate an already-open handle; that handle continues protecting its sequence until disposal.

## Reclamation rule

For one key, a committed version is retained when at least one of these is true:

- it is at or above the generic history floor;
- it is the newest version visible at an active reader boundary;
- it is the newest version visible at a persistent snapshot boundary;
- it is the newest version visible at a branch-base boundary;
- it is the latest committed version of the key.

This is a per-key reachability rule, not a single global-minimum rule.
