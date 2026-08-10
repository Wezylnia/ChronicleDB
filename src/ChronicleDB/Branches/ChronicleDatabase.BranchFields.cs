using System.Collections.Concurrent;
using ChronicleDB.Core.Identifiers;
using ChronicleDB.History.Branches;
using ChronicleDB.Storage;
using ChronicleDB.Storage.Branches;

namespace ChronicleDB;

public sealed partial class ChronicleDatabase
{
    private readonly PersistentBranchMetadataStore _branchStore;
    private readonly BranchCatalog _branches;
    private readonly ConcurrentDictionary<BranchId, BranchRuntime> _branchRuntimes;
    private readonly string _databaseDirectory;
    private readonly StorageOptions _storageOptions;
}
