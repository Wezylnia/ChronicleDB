# v0.5 isolation contract

ChronicleDB v0.5 provides **Snapshot Isolation (SI)**. It does not claim serializability.

## Guarantees

For an active transaction:

- `StartSequence` is fixed for the transaction lifetime;
- commits newer than that sequence are invisible;
- the transaction sees its own latest local write;
- uncommitted work from other transactions is never visible;
- historical tombstones obey the same sequence rule as values;
- first-committer-wins prevents two transactions with an overlapping written key from both committing after one has produced a version newer than the other's start sequence;
- one committed multi-key write set is observed atomically at a commit boundary.

Conflict validation and commit decision are serialized by the v0.5 commit coordinator, so two same-key writers cannot both pass validation against the same stale head.

## Permitted anomaly: write skew

SI is weaker than serializability. Two transactions may read the same invariant-bearing state but write disjoint keys and both commit.

Example:

1. T1 and T2 both start with `A = 1, B = 1`.
2. T1 reads both and writes `A = 0`.
3. T2 reads both and writes `B = 0`.
4. Their write sets do not overlap, so both can commit.
5. Final state is `A = 0, B = 0`.

The test suite keeps explicit coverage for this behavior so ChronicleDB is not accidentally documented as serializable.
