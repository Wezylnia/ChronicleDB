using ChronicleDB.ReferenceModel;

namespace ChronicleDB.CorrectnessTests;

public sealed class HistoricalDifferentialTests
{
    [Fact]
    public void GeneratedHistoricalReadsAndPersistentSnapshotsMatchReferenceModelAcrossRestart()
    {
        for (var seed = 101; seed <= 108; seed++)
        {
            RunSeed(seed);
        }
    }

    private static void RunSeed(int seed)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "chronicle-history-differential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ChronicleDB.ChronicleDatabase? database = null;
        try
        {
            var random = new Random(seed);
            var model = new ReferenceMvccModel();
            database = ChronicleDB.ChronicleDatabase.Open(directory);
            var activeSnapshotNames = new List<string>();

            for (var round = 0; round < 80; round++)
            {
                using (var engine = database.BeginTransaction())
                using (var reference = model.BeginTransaction())
                {
                    var operationCount = random.Next(1, 5);
                    for (var operation = 0; operation < operationCount; operation++)
                    {
                        var key = new byte[] { checked((byte)random.Next(0, 16)) };
                        if (random.Next(100) < 30)
                        {
                            engine.Delete(key);
                            reference.Delete(key);
                        }
                        else
                        {
                            var value = new byte[]
                            {
                                checked((byte)round),
                                checked((byte)operation),
                                checked((byte)random.Next(0, 256))
                            };
                            engine.Put(key, value);
                            reference.Put(key, value);
                        }
                    }

                    engine.Commit();
                    var referenceSequence = reference.Commit();
                    Assert.Equal(referenceSequence, engine.CommitSequence!.Value);
                }

                if (round % 9 == 0)
                {
                    var name = $"seed-{seed}-round-{round}";
                    using var engineSnapshot = database.CreateSnapshot(name);
                    var referenceSnapshot = model.CreateSnapshot(name);
                    Assert.Equal(referenceSnapshot.Sequence, engineSnapshot.Sequence);
                    activeSnapshotNames.Add(name);
                }

                if (activeSnapshotNames.Count > 2 && round % 13 == 0)
                {
                    var name = activeSnapshotNames[0];
                    activeSnapshotNames.RemoveAt(0);
                    var info = database.ListSnapshots().Single(snapshot => snapshot.Name == name);
                    database.DeleteSnapshot(info.SnapshotId);
                    model.DeleteSnapshot(name);
                }

                CompareRandomHistoricalState(database, model, random);
                ComparePersistentSnapshots(database, model, activeSnapshotNames, random);

                if (round == 39)
                {
                    database.Dispose();
                    database = ChronicleDB.ChronicleDatabase.Open(directory);
                    ComparePersistentSnapshots(database, model, activeSnapshotNames, random);
                }
            }
        }
        finally
        {
            database?.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void CompareRandomHistoricalState(
        ChronicleDB.ChronicleDatabase database,
        ReferenceMvccModel model,
        Random random)
    {
        var current = database.CurrentCommitSequence.Value;
        var boundary = NextHistoricalBoundary(random, current);
        using var historical = database.OpenHistoricalView(boundary);
        for (var sample = 0; sample < 5; sample++)
        {
            var key = new byte[] { checked((byte)random.Next(0, 16)) };
            var engineFound = historical.TryGet(key, out var engineValue);
            var referenceFound = model.TryReadHistorical(key, boundary, out var referenceValue);
            Assert.Equal(referenceFound, engineFound);
            Assert.Equal(referenceValue, engineValue);
        }
    }

    static ulong NextHistoricalBoundary(Random random, ulong current)
    {
        if (current <= long.MaxValue - 1UL)
        {
            return checked((ulong)random.NextInt64(0, checked((long)current + 1)));
        }

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (current == ulong.MaxValue)
        {
            random.NextBytes(bytes);
            return BitConverter.ToUInt64(bytes);
        }

        var modulus = current + 1;
        var rejectionLimit = ulong.MaxValue - (ulong.MaxValue % modulus);
        ulong candidate;
        do
        {
            random.NextBytes(bytes);
            candidate = BitConverter.ToUInt64(bytes);
        }
        while (candidate >= rejectionLimit);

        return candidate % modulus;
    }

    private static void ComparePersistentSnapshots(
        ChronicleDB.ChronicleDatabase database,
        ReferenceMvccModel model,
        IReadOnlyList<string> names,
        Random random)
    {
        var referenceSnapshots = model.ListSnapshots().ToDictionary(snapshot => snapshot.Name, StringComparer.Ordinal);
        Assert.Equal(
            names.Order(StringComparer.Ordinal),
            database.ListSnapshots().Select(snapshot => snapshot.Name).Order(StringComparer.Ordinal));

        foreach (var name in names.Take(3))
        {
            using var snapshot = database.OpenSnapshot(name);
            Assert.Equal(referenceSnapshots[name].Sequence, snapshot.Sequence);
            var key = new byte[] { checked((byte)random.Next(0, 16)) };
            var engineFound = snapshot.TryGet(key, out var engineValue);
            var referenceFound = model.TryReadHistorical(key, snapshot.Sequence, out var referenceValue);
            Assert.Equal(referenceFound, engineFound);
            Assert.Equal(referenceValue, engineValue);
        }
    }
}
