# Correctness Invariants

These invariants are the release contract. Performance work may change implementation mechanisms but may not weaken them.

## Transaction and MVCC

**Atomicity.** One committed transaction becomes logically visible as a complete unit.

**No dirty committed state.** Pending, aborted, or indeterminate transaction-local writes are never treated as committed MVCC versions.

**Snapshot visibility.** A transaction reads committed versions no newer than `StartSequence`, plus its own newest local writes.

**Future-version exclusion.** A transaction, persistent snapshot, or historical view cannot observe a commit newer than its visibility boundary.

**Write-conflict safety.** Under first-committer-wins, once a key has a commit newer than another writer's start sequence, that stale writer cannot commit a mutation of the same key.

**Tombstone history.** A delete hides the key only at boundaries that include the tombstone; older retained boundaries may still see the preceding value.

## Durability and recovery

**Durability.** An acknowledged durable transaction has enough durable WAL information to reconstruct it after a supported crash.

**No phantom commit.** A transaction without a valid Commit decision is never recovered as committed.

**No post-durability abort.** Once the durable Commit boundary has succeeded, transaction abort is impossible.

**Recovery determinism.** The same valid persistent files reconstruct the same logical current and retained historical state.

**Corruption distinction.** Complete invalid persistent structures are not silently discarded as crash tails.

## History

**Snapshot stability.** Future database writes never change the values visible at a persistent snapshot's fixed sequence.

**Historical determinism.** Reopening the same retained sequence resolves the same logical key versions.

**Snapshot lifecycle atomicity.** A crash during create/delete yields either the prior complete lifecycle state or the new complete lifecycle state; no partial root is exposed.

**Observer-safe retention.** GC may reclaim obsolete history only when no generic retained range, explicit root, active transaction, open snapshot/historical handle, current state, or recovery requirement can still observe it.

## Storage and identity

**Key identity.** Full binary key bytes define identity; hashes are acceleration only.

**Database identity.** WAL and persistent snapshot metadata must match the database GUID.

**Persistent integrity.** Metadata, pages, WAL records, and snapshot records are explicitly framed/versioned/checksummed and validated before use.

**Root retention integrity.** Every active persistent snapshot has a matching active history-root record; root metadata is database-bound, checksummed, and reconciled before the database becomes usable. A deleted root never contributes to retention, while Creating/Deleting intents remain conservatively protected.

**Monotonic capabilities.** Once durable metadata says a critical persistence subsystem was initialized, its file cannot disappear and be silently recreated as empty history.

**No out-of-band adoption.** After WAL initialization, physical current keys without WAL-backed logical history are corruption, not implicit new database state.


## Branch histories

**Branch base stability.** Once a branch is active, parent fallback always resolves against the immutable parent boundary selected at creation; later parent commits never move that boundary.

**History-domain isolation.** A committed branch-local transaction publishes versions only in that branch history. Main and sibling histories are not mutated and do not directly participate in branch-local write conflicts.

**Branch tombstone correctness.** A visible local tombstone terminates lookup as absent. It must never be interpreted as a missing local version that falls back to inherited parent data.

**Branch sequence locality.** Branch commit sequences are unique and monotonically increasing within that branch `HistoryId`; equal numeric sequences in different histories are unrelated.

**Branch-base retention.** Every active branch has an independent `BranchBase` root that protects its parent history at the fixed base sequence, regardless of the lifecycle of a source snapshot.

**Branch historical stability.** A branch historical view or persistent branch snapshot resolves the same local boundary and inherited parent base regardless of later writes in Main, siblings, or the branch.

**Legacy committed-prefix compatibility.** v0.7 branch metadata prefixes are accepted only on the legacy migration path. Once branch WAL is initialized, checkpoint + identity-bound branch WAL determine committed logical history; physical branch bytes are derived state and cannot independently establish a commit.

## Branch durability invariants

**Branch WAL identity.** Every branch WAL record belongs to exactly the branch/history domain that is recovering it; cross-history replay is rejected.

**Branch durable commit.** Once a branch Commit record has crossed the configured WAL durability barrier, recovery must expose that complete transaction even if physical branch publication was interrupted.

**Branch no-phantom-commit.** A branch transaction without a valid durable Commit record never becomes committed during recovery.

**Branch deletion dependency safety.** A branch may not complete logical deletion while a persistent child or branch snapshot still requires its history.

## Maintenance invariants

**Retention reachability.** Every value observable by the generic retained range, an explicit root, or an active process observer remains reconstructable after GC.

**Floor monotonicity.** A history's generic retention floor never moves backwards and never advances past current committed history.

**Checkpoint-before-WAL-removal.** WAL history is never discarded before an equivalent retained-history checkpoint is durable.

**GC observational equivalence.** GC changes storage/reclamation state only; every still-valid observer returns the same logical value before and after the pass.

**Compaction observational equivalence.** Compaction may move physical bytes but cannot change Main, branch, snapshot, or retained historical query results.

**Copy-publish safety.** Physical replacement always leaves either the old complete representation, the new complete representation, or a recoverably distinguishable pair; recovery never requires half of each.


## Recovery-authority hardening

**Validated-primary authority.** When a complete retained-history checkpoint primary validates, an older `.previous` generation cannot become authoritative merely because cleanup of that older file fails.

**Complete-frame integrity.** A metadata frame with a complete valid footer is not a crash-truncated tail. Contradictory header/footer lengths are corruption.

**Cleanup non-authority.** Failure to remove a temporary file, stale backup, or already-deleted branch directory cannot change logical commit, retention, or checkpoint authority after the authoritative generation has been established.
