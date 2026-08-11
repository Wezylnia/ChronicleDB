if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ChronicleDB.Inspector <database-directory> [key-hex]");
    return 2;
}

byte[]? key = null;
if (args.Length == 2)
{
    try
    {
        key = Convert.FromHexString(args[1]);
    }
    catch (FormatException)
    {
        Console.Error.WriteLine("key-hex must be an even-length hexadecimal byte string.");
        return 2;
    }
}

using var database = ChronicleDB.ChronicleDatabase.Open(args[0]);
var diagnostics = database.GetDiagnostics();
var topology = database.GetHistoryTopologyDiagnostics();

Console.WriteLine($"DatabaseId: {database.DatabaseId}");
Console.WriteLine($"State: {database.State}");
Console.WriteLine($"CurrentCommitSequence: {database.CurrentCommitSequence.Value}");
Console.WriteLine($"HistoricalRetentionFloor: {database.HistoricalRetentionFloor}");
Console.WriteLine($"CurrentKeyCount: {database.Count}");
Console.WriteLine($"Versions: {diagnostics.VersionCount} (max-chain={diagnostics.MaximumVersionChainLength})");
Console.WriteLine($"DataBytes: {diagnostics.DataFileBytes}");
Console.WriteLine($"WalBytes: {diagnostics.WalFileBytes}");
Console.WriteLine($"HistoryCheckpointBytes: {diagnostics.HistoryCheckpointBytes}");
Console.WriteLine($"RootMetadataBytes: {diagnostics.HistoryRootMetadataBytes}");
Console.WriteLine($"Branches: {diagnostics.BranchCount} local-data={diagnostics.BranchLocalDataBytes} local-wal={diagnostics.BranchLocalWalBytes}");
Console.WriteLine($"Snapshots: {diagnostics.SnapshotCount}");

Console.WriteLine("\nHistories:");
PrintHistory(topology.Main);
foreach (var history in topology.Branches)
{
    PrintHistory(history);
}

Console.WriteLine("\nRetention roots:");
if (topology.RetentionRoots.Count == 0)
{
    Console.WriteLine("  <none>");
}
else
{
    foreach (var root in topology.RetentionRoots)
    {
        Console.WriteLine(
            $"  {root.RootId} kind={root.Kind} owner={root.OwnerHistoryId} protected={root.ProtectedHistoryId} " +
            $"boundary={root.Boundary} state={root.State} created={root.CreatedAt:O}");
    }
}

Console.WriteLine("\nMain snapshots:");
var mainSnapshots = database.ListSnapshots();
if (mainSnapshots.Count == 0)
{
    Console.WriteLine("  <none>");
}
else
{
    foreach (var snapshot in mainSnapshots)
    {
        Console.WriteLine(
            $"  {snapshot.SnapshotId} name={EscapeText(snapshot.Name)} sequence={snapshot.Sequence} created={snapshot.CreatedAt:O}");
    }
}

if (key is not null)
{
    Console.WriteLine($"\nKey {Convert.ToHexString(key)}:");
    PrintValue("main:current", database.TryGet(key, out var current), current);
    foreach (var snapshot in mainSnapshots)
    {
        using var handle = database.OpenSnapshot(snapshot.SnapshotId);
        PrintValue($"main:snapshot:{EscapeText(snapshot.Name)}@{snapshot.Sequence}", handle.TryGet(key, out var value), value);
    }

    foreach (var branchInfo in database.ListBranches())
    {
        using var branch = database.OpenBranch(branchInfo.BranchId);
        PrintValue($"branch:{EscapeText(branchInfo.Name)}:current", branch.TryGet(key, out var branchValue), branchValue);
        foreach (var snapshot in branch.ListSnapshots())
        {
            using var handle = branch.OpenSnapshot(snapshot.SnapshotId);
            PrintValue(
                $"branch:{EscapeText(branchInfo.Name)}:snapshot:{EscapeText(snapshot.Name)}@{snapshot.Sequence}",
                handle.TryGet(key, out var value),
                value);
        }
    }
}

return 0;

static void PrintHistory(ChronicleDB.ChronicleHistoryDiagnostics history)
{
    var ancestry = history.ParentHistoryId is null
        ? "root"
        : $"parent={history.ParentHistoryId} base={history.ParentBaseSequence}";
    Console.WriteLine(
        $"  {history.Kind} {EscapeText(history.Name ?? "<unnamed>")} history={history.HistoryId} branch={history.BranchId?.ToString() ?? "-"} " +
        $"depth={history.Depth} {ancestry} current={history.CurrentSequence} floor={history.RetentionFloor} " +
        $"keys={history.LocalCurrentKeyCount} versions={history.VersionCount} chains={history.VersionChainCount} " +
        $"max-chain={history.MaximumVersionChainLength} snapshots={history.SnapshotCount} " +
        $"data={history.DataFileBytes} wal={history.WalFileBytes} active-tx={history.ActiveTransactionCount} " +
        $"open-branch={history.OpenBranchHandleCount} open-history={history.OpenHistoricalHandleCount} " +
        $"retention-handles={history.OpenRetentionBoundaryCount}");
}

static string EscapeText(string value)
{
    ArgumentNullException.ThrowIfNull(value);
    var builder = new System.Text.StringBuilder(value.Length);
    foreach (var character in value)
    {
        switch (character)
        {
            case '\r':
                builder.Append("\\r");
                break;
            case '\n':
                builder.Append("\\n");
                break;
            case '\t':
                builder.Append("\\t");
                break;
            default:
                var category = char.GetUnicodeCategory(character);
                if (char.IsControl(character) || category == System.Globalization.UnicodeCategory.Format)
                {
                    builder.Append("\\u");
                    builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(character);
                }
                break;
        }
    }

    return builder.ToString();
}

static void PrintValue(string label, bool found, byte[] value)
    => Console.WriteLine($"  {label}: {(found ? Convert.ToHexString(value) : "<absent>")}");
