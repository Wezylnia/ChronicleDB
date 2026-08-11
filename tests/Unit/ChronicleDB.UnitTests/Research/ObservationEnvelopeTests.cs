using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ObservationEnvelopeTests
{
    [Fact]
    public void LogicalDataObservationCanonicalizesEntriesByBinaryKey()
    {
        var historyId = HistoryId.New();
        var observation = new LogicalDataObservation(
            historyId,
            new CommitSequence(7),
            [
                new ObservedEntry([0x02], [0x20], isTombstone: false),
                new ObservedEntry([0x01], [], isTombstone: true),
            ]);

        Assert.Equal(historyId, observation.HistoryId);
        Assert.Equal(new CommitSequence(7), observation.Boundary);
        Assert.Equal([0x01], observation.Entries[0].Key.ToArray());
        Assert.True(observation.Entries[0].IsTombstone);
        Assert.Equal([0x02], observation.Entries[1].Key.ToArray());
    }

    [Fact]
    public void LogicalDataObservationRejectsDuplicateKeys()
    {
        var entries = new[]
        {
            new ObservedEntry([0x01], [0x10], isTombstone: false),
            new ObservedEntry([0x01], [0x20], isTombstone: false),
        };

        Assert.Throws<ArgumentException>(
            () => new LogicalDataObservation(HistoryId.New(), new CommitSequence(1), entries));
    }

    [Fact]
    public void ObservationEnvelopeCanonicalizesTopologyRootsAndSequences()
    {
        var main = HistoryId.New();
        var branch = HistoryId.New();
        var root = HistoryRootId.New();
        var envelope = new ObservationEnvelope(
            logicalData: null,
            historyTopology:
            [
                new HistoryTopologyObservation(branch, main, new CommitSequence(2), ObservedHistoryLifecycle.Active),
                new HistoryTopologyObservation(main, null, null, ObservedHistoryLifecycle.Active),
            ],
            rootLifecycle:
            [
                new RootLifecycleObservation(
                    root,
                    ObservedRootKind.BranchBase,
                    branch,
                    main,
                    new CommitSequence(2),
                    ObservedRootLifecycle.Active),
            ],
            new AuthorityObservation(3, 4, "checkpoint+wal"),
            [
                new SequenceObservation(branch, new CommitSequence(2), new CommitSequence(1)),
                new SequenceObservation(main, new CommitSequence(7), new CommitSequence(3)),
            ],
            new AvailabilityObservation(ObservationAvailability.Ready),
            new ErrorObservation(ObservationErrorKind.None, null),
            new CorruptionObservation(false, null),
            new SafetyPredicateObservation(true, true, true, true, true, true));

        Assert.Equal(main, envelope.HistoryTopology[0].HistoryId);
        Assert.Equal(branch, envelope.HistoryTopology[1].HistoryId);
        Assert.Equal(main, envelope.Sequences[0].HistoryId);
        Assert.Equal(root, envelope.RootLifecycle[0].RootId);
        Assert.Equal(ObservationAvailability.Ready, envelope.Availability.State);
    }

    [Fact]
    public void ObservedEntryCopiesCallerBuffers()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0x10 };
        var entry = new ObservedEntry(key, value, isTombstone: false);

        key[0] = 0xff;
        value[0] = 0xff;

        Assert.Equal([0x01], entry.Key.ToArray());
        Assert.Equal([0x10], entry.Value.ToArray());
    }
}
