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
