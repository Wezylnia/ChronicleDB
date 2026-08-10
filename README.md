# ChronicleDB

ChronicleDB is an experimental embedded, persistent, versioned key-value storage engine for .NET 10. It is designed around MVCC, snapshot isolation, crash recovery, persistent history, time-travel reads, copy-on-write branching, safe reclamation, and replaceable concurrent indexes.

The repository currently implements the v0.3 correctness baseline: append-only persistent storage, checksummed WAL-backed atomic transactions, crash recovery, immutable committed version chains, transaction start/commit sequences, Snapshot Isolation, first-committer-wins write conflicts, a managed baseline version index, deterministic reference-model testing, and crash/fault injection. Branching, advanced historical retention/GC, native memory, EBR, and latch-free indexing remain later-release work.

## Start here

- [Project definition](project-definition.md)
- [Architecture](ARCHITECTURE.md)
- [Architecture decisions](docs/adr/README.md)
- [Reference repository review](docs/architecture/reference-repository-review.md)
- [v0.1 storage format](docs/architecture/STORAGE_FORMAT.md)
- [v0.3 WAL format](docs/architecture/WAL_FORMAT.md)
- [v0.3 transaction state](docs/architecture/TRANSACTION_STATE.md)
- [v0.3 transaction commit](docs/architecture/TRANSACTION_COMMIT.md)
- [v0.3 recovery](docs/architecture/RECOVERY.md)
- [v0.3 MVCC](docs/architecture/MVCC.md)
- [v0.3 isolation contract](docs/architecture/ISOLATION.md)
- [v0.2 crash harness](docs/architecture/CRASH_HARNESS.md)

The detailed v0.5, v1.0, and v1.5 working plans are intentionally kept in the local, git-ignored `private-docs/` directory.

## Build

```powershell
dotnet restore ChronicleDB.slnx
dotnet build ChronicleDB.slnx --no-restore
dotnet test tests/Architecture/ChronicleDB.ArchitectureTests/ChronicleDB.ArchitectureTests.csproj --no-build
```

The SDK is pinned by `global.json`. Package versions, compiler settings, analyzer policy, artifact paths, and the default unsafe-code prohibition are centralized at the repository root.
