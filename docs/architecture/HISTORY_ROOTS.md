# History Roots

ChronicleDB treats historical retention as a first-class semantic contract. A history root identifies a stable boundary that must remain reconstructable even when the generic time-travel floor advances. Persistent snapshots and branch bases are durable roots; active transactions and open historical/snapshot handles contribute process-local boundaries through the same retention planner.

## Root identity

Every durable root has:

- a globally unique `HistoryRootId`;
- the owning database identity;
- an owning `HistoryId`;
- the `ProtectedHistoryId` whose versions must remain reconstructable;
- a visibility boundary expressed in the protected history's commit-sequence namespace;
- a root kind;
- an explicit lifecycle state;
- creation metadata;
- parent-history identity where ancestry is meaningful.

Main uses a stable history identity derived from the database identity. A raw commit sequence is never sufficient to identify historical state without its history domain.

For a persistent snapshot, owner and protected history are the same. For a `BranchBase`, the child branch owns the root while the **parent** history is protected at the child's fixed base sequence. This ownership/protection distinction is required for correct branch-aware reclamation.

## Lifecycle

The semantic registry models `Creating`, `Active`, `Deleting`, and `Deleted`.

- `Creating` retains the boundary while durable creation is incomplete.
- `Active` is externally usable and contributes a retention requirement.
- `Deleting` continues to retain history until durable deletion completes.
- `Deleted` no longer retains history and may later be pruned from canonical metadata.

Creation/deletion metadata is persisted as checksummed append-only lifecycle records. Recovery never exposes a partially created root as active and never guesses the outcome of a complete-but-corrupt record.

## Persistent registry

`chronicle.history-roots` is database-bound and main-history-bound. Records are checksummed and ordered by a contiguous event sequence. Root IDs are not reused, and delete records repeat immutable identity fields so replay can reject mismatched lifecycle transitions.

The database metadata journal contains `HistoryRootStoreInitializedFlag`. Once the capability is durable, a missing root registry is corruption rather than an instruction to recreate an empty file. Legacy snapshot stores are upgraded by bootstrapping equivalent active roots before the capability is published.

v1.0 maintenance may compact this append-only lifecycle journal to canonical active state after durable reclamation decisions. The semantic meaning of surviving roots is unchanged.

## Snapshot/root reconciliation

Snapshot metadata and root metadata live in separate durable streams. Open reconciliation closes crash windows between them:

1. an active snapshot without its root receives the missing root record when the snapshot create is authoritative;
2. an active snapshot whose corresponding root is durably deleted is corruption;
3. an orphaned active snapshot root whose snapshot no longer exists is durably deleted;
4. immutable field mismatches are corruption.

The same ownership rules extend to branch snapshots and branch-base roots.

## Retention queries

`HistoryRootRegistry` is the authoritative durable-root source for retention analysis. It exposes:

- active/transitional retaining roots;
- owner and protected history domains;
- generic per-history floors;
- explicit older protected boundaries;
- branch ancestry dependencies;
- explainable `HistoryRetentionRequirement` records.

The generic floor and explicit roots intentionally remain separate. One ancient branch base may preserve the exact parent versions needed at that boundary without pinning every unrelated intermediate version between that boundary and the current generic floor.

GC combines these durable requirements with process-local active-reader boundaries and performs per-key visibility analysis before a version becomes reclaimable. Deleting a root therefore removes one retention reason; it does not imply immediate destructive deletion of physical bytes.

## Branch dependency direction

An activated branch owns its base root independently of any source named snapshot. Deleting the source snapshot cannot invalidate the branch. Nested branches repeat the same immediate-parent dependency rule, and branch deletion is rejected while a child or retained branch snapshot still depends on that history.

History defines root semantics and lifecycle. Storage persists primitive root records without depending on the History assembly. The ChronicleDB facade is the composition boundary that maps the two representations and coordinates recovery/maintenance.
