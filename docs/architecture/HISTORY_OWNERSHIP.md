# History Ownership

Every persistent historical object has one creating history and an explicit retention relationship.

| Object | Created by | Shared or private | Lifetime authority |
| --- | --- | --- | --- |
| Main MVCC version | Main history | Main historical state | Main checkpoint/WAL + roots/readers |
| Branch base | child branch | references shared parent history | `BranchBase` root |
| Branch-local MVCC version | branch history | branch-private | branch checkpoint/WAL + roots/readers |
| Persistent Main snapshot | Main history | retained observer | snapshot metadata + root |
| Persistent branch snapshot | branch history | retained observer | branch snapshot metadata + root |
| Main WAL | Main | private recovery log | checkpoint/WAL lifecycle |
| `branch.wal` | one branch history | branch-private recovery log | branch checkpoint/WAL lifecycle |
| `chronicle.history` | one history domain | private recovery projection | checkpoint publication protocol |
| branch physical data | one branch history | derived/private | checkpoint/WAL; reclaimable after lifecycle safety |

Shared parent history is never made branch-private merely because a child writes the same key. The child publishes a new branch-local version or tombstone while the parent remains immutable from that branch's perspective.

A physical historical object may be reclaimed only when no current state, active process observer, persistent snapshot, branch base, child dependency, recovery protocol, or maintenance operation can still require it.
