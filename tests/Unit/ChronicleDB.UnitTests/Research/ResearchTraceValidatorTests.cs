using ChronicleDB.Core.Identifiers;
using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ResearchTraceValidatorTests
{
    [Fact]
    public void ValidOperationLifecycleIsAccepted()
    {
        var operation = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var events =
            new[]
            {
                CreateEvent(1, ResearchEventKind.OperationStarted, operation),
                CreateEvent(2, ResearchEventKind.DurabilityBarrier, operation, [1]),
                CreateEvent(3, ResearchEventKind.OperationCompleted, operation, [2]),
            };

        ResearchTraceValidator.Validate(events);
    }

    [Fact]
    public void CompletionBeforeStartIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ResearchTraceValidator.Validate([CreateEvent(1, ResearchEventKind.OperationCompleted, Guid.NewGuid())]));
    }

    [Fact]
    public void DependencyNotEmittedEarlierIsRejected()
    {
        var operation = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            ResearchTraceValidator.Validate([CreateEvent(2, ResearchEventKind.OperationStarted, operation, [1])]));
    }

    [Fact]
    public void DuplicateOperationStartIsRejected()
    {
        var operation = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => ResearchTraceValidator.Validate(
            [
                CreateEvent(1, ResearchEventKind.OperationStarted, operation),
                CreateEvent(2, ResearchEventKind.OperationStarted, operation, [1]),
            ]));
    }

    private static ResearchEvent CreateEvent(
        long id,
        ResearchEventKind kind,
        Guid operation,
        IEnumerable<long>? dependencies = null)
        => new(
            id,
            id,
            kind,
            new HistoryId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            null,
            operation,
            null,
            ["main-data"],
            kind == ResearchEventKind.DurabilityBarrier
                ? ResearchDurabilityPhase.StableStorageBarrier
                : ResearchDurabilityPhase.None,
            1,
            dependencies ?? [],
            null,
            null,
            null,
            null);
}
