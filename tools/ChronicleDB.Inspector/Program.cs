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
Console.WriteLine($"DatabaseId: {database.DatabaseId}");
Console.WriteLine($"State: {database.State}");
Console.WriteLine($"CurrentCommitSequence: {database.CurrentCommitSequence.Value}");
Console.WriteLine($"HistoricalRetentionFloor: {database.HistoricalRetentionFloor}");
Console.WriteLine($"CurrentKeyCount: {database.Count}");
Console.WriteLine($"Versions: {diagnostics.VersionCount} (max-chain={diagnostics.MaximumVersionChainLength})");
Console.WriteLine($"DataBytes: {diagnostics.DataFileBytes}");
Console.WriteLine($"WalBytes: {diagnostics.WalFileBytes}");
Console.WriteLine($"Snapshots: {diagnostics.SnapshotCount}");
foreach (var snapshot in database.ListSnapshots())
{
    Console.WriteLine(
        $"  {snapshot.SnapshotId} name={snapshot.Name} sequence={snapshot.Sequence} created={snapshot.CreatedAt:O}");
}

if (key is not null)
{
    PrintValue("current", database.TryGet(key, out var current), current);
    foreach (var snapshot in database.ListSnapshots())
    {
        using var handle = database.OpenSnapshot(snapshot.SnapshotId);
        PrintValue($"snapshot:{snapshot.Name}", handle.TryGet(key, out var value), value);
    }
}

return 0;

static void PrintValue(string label, bool found, byte[] value)
    => Console.WriteLine($"{label}: {(found ? Convert.ToHexString(value) : "<absent>")}");
