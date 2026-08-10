# v0.6 generalized history roots

ChronicleDB v0.6 makes historical retention a first-class semantic contract. A history root identifies a stable boundary that must remain reconstructable. Persistent snapshots are the first root kind; branch-base roots and active transaction boundaries use the same model in later stages.

## Root identity

Every root has:

- a globally unique `HistoryRootId`;
- an owning database identity;
- a `HistoryId` domain, because a raw commit sequence has meaning only inside a history;
- a visibility boundary;
- a root kind;
- a lifecycle state;
- creation metadata;
- an optional parent history identity for future branches.

The main database uses a stable history identity derived from its database identity. Snapshot IDs are mapped one-to-one to root IDs during the v0.5-to-v0.6 compatibility bootstrap; no existing snapshot bytes need to be rewritten.

## Lifecycle

The semantic registry models `Creating`, `Active`, `Deleting`, and `Deleted` states.

- `Creating` retains the boundary while a create intent is in flight.
- `Active` is externally visible and contributes a retention requirement.
- `Deleting` still contributes a requirement until durable deletion completes.
- `Deleted` remains as a tombstone in the registry and contributes no retention requirement.

The durable root file publishes only complete Active and Deleted records. This gives crash recovery an atomic outcome: an incomplete final frame is discarded, while a complete flushed frame is replayed even if the caller did not receive acknowledgement.

## Persistent registry

`chronicle.history-roots` is an append-only, database-bound file. Its header stores the database and main-history identities. Fixed-size records are checksummed and ordered by a contiguous event sequence. Root IDs cannot be reused, and a delete record must repeat the immutable metadata from its create record.

The database metadata journal contains `HistoryRootStoreInitializedFlag`. Once published, deleting the root file is corruption rather than a request to silently recreate an empty registry. Existing v0.5 databases without the flag are upgraded by creating the file and bootstrapping one active root for every active persistent snapshot before publishing the new capability flag.

## Reconciliation

Snapshot metadata and root metadata are published in two separate durable streams. Open reconciliation closes the small crash window:

1. an active snapshot with no root receives a root Create record;
2. an active snapshot whose root is tombstoned is corruption;
3. an active snapshot root with no active snapshot receives a root Delete record;
4. root metadata with mismatched immutable fields is corruption.

This protocol never exposes a snapshot without a retention root and never retains an orphaned snapshot root indefinitely after a successful open.

## Retention queries

`HistoryRootRegistry` is the authoritative semantic source for retention decisions. It can answer:

- which roots are active or in a transitional retaining state;
- which history domain each root protects;
- the conservative minimum protected sequence for a domain;
- explainable `HistoryRetentionRequirement` records for diagnostics and future GC.

The v0.6 physical engine remains conservative: deleting a root does not reclaim pages or MVCC versions. Later reclamation work consumes these requirements rather than inventing a second retention rule.

## Dependency direction

History defines root meaning and lifecycle semantics. Storage persists primitive root fields without referencing the History assembly. `ChronicleDB` composes both layers and is the only place that maps storage records to semantic roots.


## v0.7 branch-base interpretation

A `BranchBase` root is owned by the child branch history but protects a boundary in its **parent** history. Therefore `HistoryRoot.HistoryId` identifies the root owner, while `ProtectedHistoryId` is the parent for `BranchBase` and the owner history for snapshot roots. Retention queries filter and compute floors against `ProtectedHistoryId`; confusing ownership with protected history would make branch-aware GC unsound.

An activated branch owns its base root independently of any source named snapshot. Deleting that source snapshot removes only the snapshot's retention requirement; the branch-base root continues protecting the inherited parent boundary. Nested branches form the same immediate-parent dependency chain.
