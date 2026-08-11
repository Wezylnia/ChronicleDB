# Tools

Tools are development and research clients of ChronicleDB, not alternate engine implementations.

- `ChronicleDB.CrashHarness` terminates child processes at durability-sensitive fault points and validates reopen state.
- `ChronicleDB.WorkloadRunner` executes deterministic multi-history workloads with differential checks.
- `ChronicleDB.Inspector` reports topology, retention roots, storage/WAL sizes, and optional key observations. Persisted names are escaped before terminal output.

Example crash campaign:

```powershell
dotnet run -c Release --project tools/ChronicleDB.CrashHarness -- run 100
```
