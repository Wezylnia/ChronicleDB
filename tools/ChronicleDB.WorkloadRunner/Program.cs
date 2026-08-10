if (args.Length > 0 && !args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: run [directory] [seed] [operations]");
    return 2;
}

var directory = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(Path.GetTempPath(), "chronicle-workload-" + Guid.NewGuid().ToString("N"));
var seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 17;
var operationCount = args.Length > 3 && int.TryParse(args[3], out var parsedCount) ? parsedCount : 100;
if (operationCount is < 1 or > 100_000)
{
    Console.Error.WriteLine("operations must be between 1 and 100000.");
    return 2;
}

var ownsDirectory = args.Length <= 1;
try
{
    Directory.CreateDirectory(directory);
    var model = new ChronicleDB.ReferenceModel.ReferenceKeyValueModel();
    using var database = ChronicleDB.ChronicleDatabase.Open(directory);
    var random = new Random(seed);
    for (var index = 0; index < operationCount; index++)
    {
        var key = new[] { (byte)random.Next(0, 16) };
        if (random.Next(4) == 0)
        {
            var expected = model.Delete(key);
            var actual = database.Delete(key);
            if (expected != actual)
            {
                throw new InvalidOperationException($"Delete mismatch at operation {index}.");
            }
        }
        else
        {
            var value = new[] { (byte)random.Next(0, 256), (byte)index };
            model.Put(key, value);
            database.Put(key, value);
        }

        if (database.Count != model.Count)
        {
            throw new InvalidOperationException($"Count mismatch at operation {index}.");
        }
    }

    Console.WriteLine($"ok seed={seed} operations={operationCount} keys={model.Count}");
    return 0;
}
finally
{
    if (ownsDirectory && Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}
