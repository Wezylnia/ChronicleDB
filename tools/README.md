# Tools

Tools are executable development surfaces, not alternate engine implementations. They consume the supported facade or shared codecs and must not duplicate storage, WAL, visibility, or recovery logic.

The crash harness can be run after building the solution with:

```powershell
dotnet run --project tools/ChronicleDB.CrashHarness -- run
```
