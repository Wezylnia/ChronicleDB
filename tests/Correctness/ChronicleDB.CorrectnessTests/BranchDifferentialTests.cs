using ChronicleDB.ReferenceModel;

namespace ChronicleDB.CorrectnessTests;

public sealed class BranchDifferentialTests
{
    [Theory]
    [InlineData(19)]
    [InlineData(113)]
    [InlineData(90210)]
    public void GeneratedMainAndSiblingBranchHistoriesMatchReferenceModel(int seed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronicle-branch-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = ChronicleDB.ChronicleDatabase.Open(directory);
            var reference = new ReferenceBranchingModel();
            var random = new Random(seed);

            // Establish a non-empty shared base.
            for (byte key = 1; key <= 8; key++)
            {
                database.Put([key], [key]);
                using var tx = reference.Begin("main");
                tx.Put([key], [key]);
                tx.Commit();
            }

            using var branchA = database.CreateBranch("A");
            using var branchB = database.CreateBranch("B");
            reference.CreateBranch("main", reference.CurrentSequence("main"), "A");
            reference.CreateBranch("main", reference.CurrentSequence("main"), "B");
            var branches = new Dictionary<string, ChronicleDB.ChronicleBranch>(StringComparer.Ordinal)
            {
                ["A"] = branchA,
                ["B"] = branchB,
            };
            var retainedSnapshots = new List<(ChronicleDB.ChronicleBranchSnapshot Snapshot, string History, ulong Sequence)>();

            for (var operation = 0; operation < 500; operation++)
            {
                var history = random.Next(3) switch
                {
                    0 => "main",
                    1 => "A",
                    _ => "B",
                };
                var key = checked((byte)random.Next(1, 17));
                var delete = random.Next(5) == 0;
                var abort = random.Next(11) == 0;
                var value = new byte[] { checked((byte)random.Next(0, 256)), checked((byte)(operation % 256)) };

                if (history == "main")
                {
                    using var actual = database.BeginTransaction();
                    using var expected = reference.Begin("main");
                    if (delete)
                    {
                        actual.Delete([key]);
                        expected.Delete([key]);
                    }
                    else
                    {
                        actual.Put([key], value);
                        expected.Put([key], value);
                    }
                    if (abort)
                    {
                        actual.Abort();
                        expected.Abort();
                    }
                    else
                    {
                        actual.Commit();
                        expected.Commit();
                    }
                }
                else
                {
                    using var actual = branches[history].BeginTransaction();
                    using var expected = reference.Begin(history);
                    if (delete)
                    {
                        actual.Delete([key]);
                        expected.Delete([key]);
                    }
                    else
                    {
                        actual.Put([key], value);
                        expected.Put([key], value);
                    }
                    if (abort)
                    {
                        actual.Abort();
                        expected.Abort();
                    }
                    else
                    {
                        actual.Commit();
                        expected.Commit();
                    }
                }

                if (operation % 31 == 0)
                {
                    var snapshotHistory = random.Next(2) == 0 ? "A" : "B";
                    var snapshotBranch = branches[snapshotHistory];
                    var snapshot = snapshotBranch.CreateSnapshot($"generated-{seed}-{operation}");
                    retainedSnapshots.Add((snapshot, snapshotHistory, snapshot.Info.Sequence));
                }

                if (operation % 7 == 0)
                {
                    CompareCurrent(database, branches, reference);
                }
                if (operation % 23 == 0)
                {
                    CompareHistorical(branchA, "A", reference, random);
                    CompareHistorical(branchB, "B", reference, random);
                }
                if (operation % 37 == 0)
                {
                    CompareSnapshots(retainedSnapshots, reference);
                }
            }

            CompareCurrent(database, branches, reference);
            CompareSnapshots(retainedSnapshots, reference);
            foreach (var retained in retainedSnapshots)
            {
                retained.Snapshot.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static void CompareCurrent(
        ChronicleDB.ChronicleDatabase database,
        IReadOnlyDictionary<string, ChronicleDB.ChronicleBranch> branches,
        ReferenceBranchingModel reference)
    {
        for (byte key = 1; key <= 16; key++)
        {
            AssertSame(
                reference.TryRead("main", reference.CurrentSequence("main"), [key], out var expectedMain),
                expectedMain,
                database.TryGet([key], out var actualMain),
                actualMain);
            foreach (var (name, branch) in branches)
            {
                AssertSame(
                    reference.TryRead(name, reference.CurrentSequence(name), [key], out var expected),
                    expected,
                    branch.TryGet([key], out var actual),
                    actual);
            }
        }
    }

    private static void CompareHistorical(
        ChronicleDB.ChronicleBranch branch,
        string name,
        ReferenceBranchingModel reference,
        Random random)
    {
        var current = reference.CurrentSequence(name);
        var sequence = current == 0 ? 0UL : checked((ulong)random.NextInt64(0, checked((long)current + 1)));
        using var view = branch.OpenHistoricalView(sequence);
        for (byte key = 1; key <= 16; key++)
        {
            AssertSame(
                reference.TryRead(name, sequence, [key], out var expected),
                expected,
                view.TryGet([key], out var actual),
                actual);
        }
    }

    private static void CompareSnapshots(
        IReadOnlyList<(ChronicleDB.ChronicleBranchSnapshot Snapshot, string History, ulong Sequence)> snapshots,
        ReferenceBranchingModel reference)
    {
        foreach (var retained in snapshots)
        {
            for (byte key = 1; key <= 16; key++)
            {
                AssertSame(
                    reference.TryRead(retained.History, retained.Sequence, [key], out var expected),
                    expected,
                    retained.Snapshot.TryGet([key], out var actual),
                    actual);
            }
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
