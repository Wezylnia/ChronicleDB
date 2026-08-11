using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class CanonicalObservationTraceTests
{
    [Fact]
    public void ConsecutiveStutteringObservationsCollapse()
    {
        var point = CreatePoint(ResearchEventKind.HistoryReady, "state-a");

        var trace = new CanonicalObservationTrace([point, point, point]);

        Assert.Single(trace.Points);
    }

    [Fact]
    public void AvailabilityAndSafetyDifferencesAreNotEquivalent()
    {
        var first = new CanonicalObservationTrace(
            [CreatePoint(ResearchEventKind.HistoryReady, "state-a")]);
        var second = new CanonicalObservationTrace(
            [CreatePoint(
                ResearchEventKind.HistoryReady,
                "state-a",
                availability: ObservationAvailability.Unavailable,
                safety: SafetyPredicateMask.NoPhantomCommit)]);

        Assert.False(first.EquivalentTo(second));
    }

    [Fact]
    public void LogicalStateDigestDifferencesAreNotEquivalent()
    {
        var first = new CanonicalObservationTrace(
            [CreatePoint(ResearchEventKind.RecoveryCompleted, "state-a")]);
        var second = new CanonicalObservationTrace(
            [CreatePoint(ResearchEventKind.RecoveryCompleted, "state-b")]);

        Assert.False(first.EquivalentTo(second));
    }

    [Fact]
    public void ObservationPointRejectsUnknownHistory()
    {
        Assert.Throws<ArgumentException>(
            () => new ObservationTracePoint(
                ResearchEventKind.HistoryReady,
                HistoryId.Empty,
                ResearchDurabilityPhase.AuthorityPublished,
                ObservationAvailability.Ready,
                ObservationErrorKind.None,
                false,
                1,
                SafetyPredicateMask.None,
                null,
                null));
    }

    [Fact]
    public void FromEnvelopeDerivesStableLogicalStateDigest()
    {
        var historyId = new HistoryId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var envelope = new ObservationEnvelope(
            new LogicalDataObservation(
                historyId,
                new ChronicleDB.Core.Sequences.CommitSequence(2),
                [new ObservedEntry([0x01], [0x02], isTombstone: false)]),
            [new HistoryTopologyObservation(historyId, null, null, ObservedHistoryLifecycle.Active)],
            [],
            new AuthorityObservation(1, 2, "checkpoint+wal"),
            [new SequenceObservation(historyId, new ChronicleDB.Core.Sequences.CommitSequence(2), ChronicleDB.Core.Sequences.CommitSequence.Initial)],
            new AvailabilityObservation(ObservationAvailability.Ready),
            new ErrorObservation(ObservationErrorKind.None, null),
            new CorruptionObservation(false, null),
            new SafetyPredicateObservation(true, true, true, true, true, true));

        var point = ObservationTracePoint.FromEnvelope(
            ResearchEventKind.HistoryReady,
            historyId,
            ResearchDurabilityPhase.AuthorityPublished,
            2,
            envelope);

        Assert.Equal(64, point.LogicalStateDigest?.Length);
        Assert.Equal(ObservationEnvelopeFingerprint.Compute(envelope), point.LogicalStateDigest);
    }

    private static ObservationTracePoint CreatePoint(
        ResearchEventKind eventKind,
        string digest,
        ObservationAvailability availability = ObservationAvailability.Ready,
        SafetyPredicateMask safety = SafetyPredicateMask.NoPhantomCommit
            | SafetyPredicateMask.NoCrossHistoryReplay
            | SafetyPredicateMask.BaseStable
            | SafetyPredicateMask.NoInvalidRoot
            | SafetyPredicateMask.NoPrematureReclaim
            | SafetyPredicateMask.NoEarlyPublication)
        => new(
            eventKind,
            new HistoryId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            ResearchDurabilityPhase.AuthorityPublished,
            availability,
            ObservationErrorKind.None,
            false,
            1,
            safety,
            digest,
            null);
}
