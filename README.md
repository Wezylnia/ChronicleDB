# ChronicleDB

ChronicleDB is an experimental embedded, persistent, versioned key-value storage engine for .NET 10. It is designed around MVCC, snapshot isolation, crash recovery, persistent history, time-travel reads, copy-on-write branching, safe reclamation, and replaceable concurrent indexes.

The repository currently contains the architecture baseline, owned-key/MVCC/index primitives, and the v0.1 append-only persistent key-value foundation. Transactions, WAL-backed atomicity, recovery, and maintenance are implemented in the staged order defined by the project plans.

## Start here

- [Project definition](project-definition.md)
- [Architecture](ARCHITECTURE.md)
- [Architecture decisions](docs/adr/README.md)
- [Reference repository review](docs/architecture/reference-repository-review.md)
- [v0.1 storage format](docs/architecture/STORAGE_FORMAT.md)
- [v0.2 WAL format](docs/architecture/WAL_FORMAT.md)
- [v0.2 transaction state](docs/architecture/TRANSACTION_STATE.md)
- [v0.2 transaction commit](docs/architecture/TRANSACTION_COMMIT.md)
- [v0.2 recovery](docs/architecture/RECOVERY.md)

The detailed v0.5, v1.0, and v1.5 working plans are intentionally kept in the local, git-ignored `private-docs/` directory.

## Build

```powershell
dotnet restore ChronicleDB.slnx
dotnet build ChronicleDB.slnx --no-restore
dotnet test tests/Architecture/ChronicleDB.ArchitectureTests/ChronicleDB.ArchitectureTests.csproj --no-build
```

The SDK is pinned by `global.json`. Package versions, compiler settings, analyzer policy, artifact paths, and the default unsafe-code prohibition are centralized at the repository root.
