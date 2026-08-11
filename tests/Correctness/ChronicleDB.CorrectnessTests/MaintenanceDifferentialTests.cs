using ChronicleDB.Maintenance;
using ChronicleDB.ReferenceModel;

namespace ChronicleDB.CorrectnessTests;

public sealed class MaintenanceDifferentialTests
{
    [Theory]
    [InlineData(31)]
    [InlineData(73)]
    [InlineData(211)]
    public void GarbageCollectionAndCompactionPreserveEveryRetainedObserver(int seed)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "chronicle-maintenance-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var reference = new ReferenceBranchingModel();
            var random = new Random(seed);
            Guid branchAId;
            Guid branchBId;
            var mainSnapshots = new List<(Guid Id, ulong Sequence)>();
            var branchSnapshots = new List<(string History, Guid BranchId, Guid Id, ulong Sequence)>();
            ulong branchAHistoricalSequence;
            ulong branchBHistoricalSequence;

            using (var database = ChronicleDB.ChronicleDatabase.Open(directory))
            {
                for (byte key = 1; key <= 12; key++)
                {
                    database.Put([key], [key, 0xA5]);
                    using var expected = reference.Begin("main");
                    expected.Put([key], [key, 0xA5]);
                    expected.Commit();
                }

                using var branchA = database.CreateBranch("A");
                using var branchB = database.CreateBranch("B");
                branchAId = branchA.BranchId;
                branchBId = branchB.BranchId;
                reference.CreateBranch("main", reference.CurrentSequence("main"), "A");
                reference.CreateBranch("main", reference.CurrentSequence("main"), "B");

                var branches = new Dictionary<string, ChronicleDB.ChronicleBranch>(StringComparer.Ordinal)
                {
                    ["A"] = branchA,
                    ["B"] = branchB,
                };

                for (var operation = 0; operation < 240; operation++)
                {
                    var history = random.Next(3) switch
                    {
                        0 => "main",
                        1 => "A",
                        _ => "B",
                    };
                    var key = checked((byte)random.Next(1, 25));
                    var delete = random.Next(6) == 0;
                    var abort = random.Next(17) == 0;
                    var value = new byte[]
                    {
                        checked((byte)random.Next(0, 256)),
                        checked((byte)(operation & 0xFF)),
                        checked((byte)(seed & 0xFF)),
                    };

                    if (history == "main")
                    {
                        using var actual = database.BeginTransaction();
                        using var expected = reference.Begin("main");
                        ApplyWrite(actual, expected, key, delete, value);
                        Complete(actual, expected, abort);
                    }
                    else
                    {
                        using var actual = branches[history].BeginTransaction();
                        using var expected = reference.Begin(history);
                        ApplyWrite(actual, expected, key, delete, value);
                        Complete(actual, expected, abort);
                    }

                    if (operation is 40 or 110 or 180)
                    {
                        using var snapshot = database.CreateSnapshot($"main-{seed}-{operation}");
                        mainSnapshots.Add((snapshot.SnapshotId, snapshot.Sequence));
                    }

                    if (operation is 55 or 135 or 205)
                    {
                        var historyForSnapshot = operation % 2 == 0 ? "A" : "B";
                        var owner = branches[historyForSnapshot];
                        using var snapshot = owner.CreateSnapshot($"{historyForSnapshot}-{seed}-{operation}");
                        branchSnapshots.Add((
                            historyForSnapshot,
                            owner.BranchId,
                            snapshot.Info.SnapshotId,
                            snapshot.Info.Sequence));
                    }
                }

                branchAHistoricalSequence = RecentHistoricalSequence(reference.CurrentSequence("A"));
                branchBHistoricalSequence = RecentHistoricalSequence(reference.CurrentSequence("B"));

                AssertAllObservers(
                    database,
                    branchA,
                    branchB,
                    reference,
                    mainSnapshots,
                    branchSnapshots,
                    branchAHistoricalSequence,
                    branchBHistoricalSequence);

                _ = database.RunGarbageCollection(new GarbageCollectionOptions
                {
                    RetainRecentCommits = 4,
                });
                _ = database.RunCompaction(new CompactionOptions
                {
                    MaxHistoriesPerPass = 8,
                    MinimumReclaimableBytes = 1,
                });

                AssertAllObservers(
                    database,
                    branchA,
                    branchB,
                    reference,
                    mainSnapshots,
                    branchSnapshots,
                    branchAHistoricalSequence,
                    branchBHistoricalSequence);
            }

            using var reopened = ChronicleDB.ChronicleDatabase.Open(directory);
            using var recoveredA = reopened.OpenBranch(branchAId);
            using var recoveredB = reopened.OpenBranch(branchBId);
            AssertAllObservers(
                reopened,
                recoveredA,
                recoveredB,
                reference,
                mainSnapshots,
                branchSnapshots,
                branchAHistoricalSequence,
                branchBHistoricalSequence);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static void ApplyWrite(
        ChronicleDB.ChronicleTransaction actual,
        ReferenceBranchTransaction expected,
        byte key,
        bool delete,
        byte[] value)
    {
        if (delete)
        {
            actual.Delete([key]);
            expected.Delete([key]);
            return;
        }

        actual.Put([key], value);
        expected.Put([key], value);
    }

    private static void Complete(
        ChronicleDB.ChronicleTransaction actual,
        ReferenceBranchTransaction expected,
        bool abort)
    {
        if (abort)
        {
            actual.Abort();
            expected.Abort();
            return;
        }

        actual.Commit();
        expected.Commit();
    }

    private static ulong RecentHistoricalSequence(ulong current)
        => current <= 2 ? current : current - 2;

    private static void AssertAllObservers(
        ChronicleDB.ChronicleDatabase database,
        ChronicleDB.ChronicleBranch branchA,
        ChronicleDB.ChronicleBranch branchB,
        ReferenceBranchingModel reference,
        IReadOnlyList<(Guid Id, ulong Sequence)> mainSnapshots,
        IReadOnlyList<(string History, Guid BranchId, Guid Id, ulong Sequence)> branchSnapshots,
        ulong branchAHistoricalSequence,
        ulong branchBHistoricalSequence)
    {
        AssertCurrent(database, branchA, branchB, reference);

        foreach (var retained in mainSnapshots)
        {
            using var snapshot = database.OpenSnapshot(retained.Id);
            for (byte key = 1; key <= 24; key++)
            {
                AssertSame(
                    reference.TryRead("main", retained.Sequence, [key], out var expected),
                    expected,
                    snapshot.TryGet([key], out var actual),
                    actual);
            }
        }

        foreach (var retained in branchSnapshots)
        {
            var branch = retained.BranchId == branchA.BranchId ? branchA : branchB;
            using var snapshot = branch.OpenSnapshot(retained.Id);
            for (byte key = 1; key <= 24; key++)
            {
                AssertSame(
                    reference.TryRead(retained.History, retained.Sequence, [key], out var expected),
                    expected,
                    snapshot.TryGet([key], out var actual),
                    actual);
            }
        }

        using (var historicalA = branchA.OpenHistoricalView(branchAHistoricalSequence))
        {
            AssertHistorical(historicalA, "A", branchAHistoricalSequence, reference);
        }

        using (var historicalB = branchB.OpenHistoricalView(branchBHistoricalSequence))
        {
            AssertHistorical(historicalB, "B", branchBHistoricalSequence, reference);
        }
    }

    private static void AssertCurrent(
        ChronicleDB.ChronicleDatabase database,
        ChronicleDB.ChronicleBranch branchA,
        ChronicleDB.ChronicleBranch branchB,
        ReferenceBranchingModel reference)
    {
        for (byte key = 1; key <= 24; key++)
        {
            AssertSame(
                reference.TryRead("main", reference.CurrentSequence("main"), [key], out var expectedMain),
                expectedMain,
                database.TryGet([key], out var actualMain),
                actualMain);
            AssertSame(
                reference.TryRead("A", reference.CurrentSequence("A"), [key], out var expectedA),
                expectedA,
                branchA.TryGet([key], out var actualA),
                actualA);
            AssertSame(
                reference.TryRead("B", reference.CurrentSequence("B"), [key], out var expectedB),
                expectedB,
                branchB.TryGet([key], out var actualB),
                actualB);
        }
    }

    private static void AssertHistorical(
        ChronicleDB.ChronicleBranchHistoricalView view,
        string history,
        ulong sequence,
        ReferenceBranchingModel reference)
    {
        for (byte key = 1; key <= 24; key++)
        {
            AssertSame(
                reference.TryRead(history, sequence, [key], out var expected),
                expected,
                view.TryGet([key], out var actual),
                actual);
        }
    }

    private static void AssertSame(bool expectedFound, byte[] expected, bool actualFound, byte[] actual)
    {
        Assert.Equal(expectedFound, actualFound);
        if (expectedFound)
        {
            Assert.Equal(expected, actual);
        }
    }
}
