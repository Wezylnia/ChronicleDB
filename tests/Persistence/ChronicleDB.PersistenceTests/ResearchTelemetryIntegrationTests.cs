using ChronicleDB;
using ChronicleDB.Diagnostics.Research;
using ChronicleDB.PersistenceTests.Fixtures;

namespace ChronicleDB.PersistenceTests;

public sealed class ResearchTelemetryIntegrationTests
{
    [Fact]
    public void OpenPublishesRecoveryMilestonesWithoutChangingDefaultState()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        {
            database.Put([0x01], [0x02]);
            Assert.True(database.TryGet([0x01], out var value));
            Assert.Equal([0x02], value);
        }

        var events = sink.Snapshot();

        var recoveryMilestones = events
            .Where(researchEvent => researchEvent.EventKind is ResearchEventKind.RecoveryStarted
                or ResearchEventKind.HistoryReady
                or ResearchEventKind.RecoveryCompleted)
            .ToArray();
        Assert.Equal(
            [
                ResearchEventKind.RecoveryStarted,
                ResearchEventKind.HistoryReady,
                ResearchEventKind.RecoveryCompleted,
            ],
            recoveryMilestones.Select(researchEvent => researchEvent.EventKind));
        Assert.Empty(recoveryMilestones[0].DependencyEventIds);
        Assert.Single(recoveryMilestones[1].DependencyEventIds);
        Assert.True(recoveryMilestones[1].DependencyEventIds[0] < recoveryMilestones[1].LogicalEventId);
        Assert.Equal([recoveryMilestones[1].LogicalEventId], recoveryMilestones[2].DependencyEventIds);
        Assert.All(events, researchEvent => Assert.True(researchEvent.HistoryId.IsValid));
    }

    [Fact]
    public void TelemetrySinkFailureDoesNotAbortDatabaseOpen()
    {
        using var directory = new StorageTestDirectory();
        var sink = new ThrowingResearchEventSink();

        using var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink);

        database.Put([0x01], [0x02]);
        Assert.True(database.TryGet([0x01], out _));
    }

    [Fact]
    public void TraceLogicalIdsContinueAcrossDatabaseReopen()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        {
            database.Put([0x01], [0x02]);
        }

        var firstCount = sink.Snapshot().Count;

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        {
            database.Put([0x03], [0x04]);
        }

        var events = sink.Snapshot();
        Assert.True(firstCount > 0);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.LogicalEventId));
    }

    [Fact]
    public void CommitPublishesDurabilityAndAuthorityMilestones()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        {
            database.Put([0x01], [0x02]);
        }

        var events = sink.Snapshot();
        var commitEvents = events
            .Where(researchEvent => researchEvent.EventKind is ResearchEventKind.OperationStarted
                or ResearchEventKind.DurabilityBarrier
                or ResearchEventKind.AuthorityPublished
                or ResearchEventKind.OperationCompleted)
            .Where(researchEvent => researchEvent.TransactionId is not null)
            .ToArray();

        Assert.Equal(
            [
                ResearchEventKind.OperationStarted,
                ResearchEventKind.DurabilityBarrier,
                ResearchEventKind.AuthorityPublished,
                ResearchEventKind.OperationCompleted,
            ],
            commitEvents.Select(researchEvent => researchEvent.EventKind));
        Assert.Equal([commitEvents[0].LogicalEventId], commitEvents[1].DependencyEventIds);
        Assert.Equal([commitEvents[1].LogicalEventId], commitEvents[2].DependencyEventIds);
        Assert.Equal([commitEvents[2].LogicalEventId], commitEvents[3].DependencyEventIds);
        Assert.All(commitEvents, researchEvent => Assert.Equal(1UL, researchEvent.AuthorityGeneration));
    }


    [Fact]
    public void DeepInheritedReadPublishesProbeAndResolutionTelemetry()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink);
        database.Put([0x31], [0x41]);
        using var branchA = database.CreateBranch("read-a");
        using var branchB = branchA.CreateBranch("read-b");

        Assert.True(branchB.TryGet([0x31], out var value));
        Assert.Equal([0x41], value);

        var read = Assert.Single(sink.Snapshot(), item => item.EventKind == ResearchEventKind.HistoryReadObserved);
        Assert.NotNull(read.ReadObservation);
        Assert.Equal(ResearchReadResolutionKind.InheritedValue, read.ReadObservation.Value.ResolutionKind);
        Assert.Equal(2, read.ReadObservation.Value.AncestorProbeCount);
        Assert.Equal(2, read.ReadObservation.Value.ResolvedAncestorDepth);
        Assert.True(read.ReadObservation.Value.LocalMiss);
        Assert.False(read.ReadObservation.Value.TombstoneShadow);
        Assert.Equal(branchB.HistoryId, read.HistoryId.Value);
    }

    [Fact]
    public void BranchCommitTraceUsesBranchHistoryAndResources()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using (var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink))
        using (var branch = database.CreateBranch("trace-branch"))
        {
            branch.Put([0x01], [0x02]);
        }

        var branchEvents = sink
            .Snapshot()
            .Where(researchEvent => researchEvent.EventKind == ResearchEventKind.OperationStarted
                && researchEvent.TransactionId is not null
                && researchEvent.ResourceSet.Any(resource => resource.StartsWith("branch-", StringComparison.Ordinal)))
            .ToArray();

        Assert.Single(branchEvents);
        var branchHistoryId = branchEvents[0].HistoryId;
        Assert.NotEqual(Guid.Empty, branchHistoryId.Value);
        Assert.Contains(
            branchEvents[0].ResourceSet,
            resource => resource.EndsWith("-data", StringComparison.Ordinal));
        Assert.Contains(
            branchEvents[0].ResourceSet,
            resource => resource.EndsWith("-wal", StringComparison.Ordinal));
        Assert.NotNull(branchEvents[0].ParentHistoryId);
    }

    [Fact]
    public void BranchLifecyclePublishesCreateAndDeleteAuthorityChains()
    {
        using var directory = new StorageTestDirectory();
        var sink = new TraceResearchEventSink();

        using var database = ChronicleDatabase.Open(directory.Path, researchEventSink: sink);
        Guid historyId;
        using (var branch = database.CreateBranch("lifecycle-trace"))
        {
            historyId = branch.HistoryId;
        }
        database.DeleteBranch("lifecycle-trace");

        var lifecycleOperations = sink.Snapshot()
            .Where(item => item.HistoryId.Value == historyId && item.TransactionId is null)
            .Where(item => item.EventKind is ResearchEventKind.OperationStarted
                or ResearchEventKind.DurabilityBarrier
                or ResearchEventKind.AuthorityPublished
                or ResearchEventKind.OperationCompleted)
            .GroupBy(item => item.OperationId)
            .Select(group => group.OrderBy(item => item.LogicalEventId).ToArray())
            .Where(group => group.Length == 4)
            .ToArray();

        Assert.Equal(2, lifecycleOperations.Length);
        foreach (var operation in lifecycleOperations)
        {
            Assert.Equal(
                [
                    ResearchEventKind.OperationStarted,
                    ResearchEventKind.DurabilityBarrier,
                    ResearchEventKind.AuthorityPublished,
                    ResearchEventKind.OperationCompleted,
                ],
                operation.Select(item => item.EventKind));
            Assert.Equal([operation[0].LogicalEventId], operation[1].DependencyEventIds);
            Assert.Equal([operation[1].LogicalEventId], operation[2].DependencyEventIds);
            Assert.Equal([operation[2].LogicalEventId], operation[3].DependencyEventIds);
            Assert.Contains("branch-catalog", operation[0].ResourceSet);
            Assert.Contains("history-roots", operation[0].ResourceSet);
        }

        Assert.Equal(ResearchDurabilityPhase.AuthorityPublished, lifecycleOperations[0][3].DurabilityPhase);
        Assert.Equal(ResearchDurabilityPhase.Cleanup, lifecycleOperations[1][3].DurabilityPhase);
    }

    [Fact]
    public void BranchReopenPublishesPerHistoryRecoveryValidationMilestones()
    {
        using var directory = new StorageTestDirectory();
        Guid branchHistoryId;
        using (var database = ChronicleDatabase.Open(directory.Path))
        using (var branch = database.CreateBranch("recovery-trace"))
        {
            branchHistoryId = branch.HistoryId;
            branch.Put([0x21], [0x31]);
        }

        var sink = new TraceResearchEventSink();
        using var reopened = ChronicleDatabase.Open(directory.Path, researchEventSink: sink);

        var events = sink.Snapshot();
        var started = Assert.Single(events, item =>
            item.EventKind == ResearchEventKind.OperationStarted
            && item.HistoryId.Value == branchHistoryId
            && item.TransactionId is null);
        var validated = Assert.Single(events, item =>
            item.EventKind == ResearchEventKind.HistoryValidated
            && item.HistoryId.Value == branchHistoryId
            && item.OperationId == started.OperationId);

        Assert.Equal([started.LogicalEventId], validated.DependencyEventIds);
        Assert.Contains(started.ResourceSet, resource => resource.EndsWith("-wal", StringComparison.Ordinal));
        Assert.Contains(started.ResourceSet, resource => resource.EndsWith("-data", StringComparison.Ordinal));
        Assert.Contains("branch-catalog", validated.ResourceSet);
        Assert.Contains("history-roots", validated.ResourceSet);

        var phaseEvents = events
            .Where(item => item.HistoryId.Value == branchHistoryId
                && item.EventKind is ResearchEventKind.RecoveryPhaseStarted or ResearchEventKind.RecoveryPhaseCompleted)
            .ToArray();
        var phases = new[]
        {
            ResearchRecoveryPhaseKind.LocalStoreOpen,
            ResearchRecoveryPhaseKind.WalAuthorityOpen,
            ResearchRecoveryPhaseKind.CheckpointLoadAndReplay,
            ResearchRecoveryPhaseKind.WalReplay,
            ResearchRecoveryPhaseKind.PhysicalStateValidation,
            ResearchRecoveryPhaseKind.SnapshotMetadataOpen,
        };
        Assert.Equal(phases.Length * 2, phaseEvents.Length);
        foreach (var phase in phases)
        {
            var startedPhase = Assert.Single(phaseEvents, item =>
                item.EventKind == ResearchEventKind.RecoveryPhaseStarted
                && item.RecoveryPhaseObservation?.Phase == phase);
            var completedPhase = Assert.Single(phaseEvents, item =>
                item.EventKind == ResearchEventKind.RecoveryPhaseCompleted
                && item.OperationId == startedPhase.OperationId
                && item.RecoveryPhaseObservation?.Phase == phase);
            Assert.Equal([startedPhase.LogicalEventId], completedPhase.DependencyEventIds);
            Assert.True(startedPhase.LogicalEventId < completedPhase.LogicalEventId);
        }


        var mainHistoryId = reopened.GetHistoryTopologyDiagnostics().Main.HistoryId;
        foreach (var globalPhase in new[]
        {
            ResearchRecoveryPhaseKind.CatalogAndDependencyValidation,
            ResearchRecoveryPhaseKind.BranchRuntimesOpen,
        })
        {
            var globalStarted = Assert.Single(events, item =>
                item.HistoryId.Value == mainHistoryId
                && item.EventKind == ResearchEventKind.RecoveryPhaseStarted
                && item.RecoveryPhaseObservation?.Phase == globalPhase);
            var globalCompleted = Assert.Single(events, item =>
                item.HistoryId.Value == mainHistoryId
                && item.EventKind == ResearchEventKind.RecoveryPhaseCompleted
                && item.OperationId == globalStarted.OperationId
                && item.RecoveryPhaseObservation?.Phase == globalPhase);
            Assert.Equal([globalStarted.LogicalEventId], globalCompleted.DependencyEventIds);
        }

        Assert.True(validated.LogicalEventId < events.Single(item => item.EventKind == ResearchEventKind.RecoveryCompleted).LogicalEventId);
    }

    [Fact]
    public void ResearchRetentionSnapshotCapturesRawVersionsAndPersistentRoots()
    {
        using var directory = new StorageTestDirectory();
        using var database = ChronicleDatabase.Open(directory.Path);
        database.Put([0x51], [0x61, 0x62]);
        Guid snapshotId;
        using (var snapshot = database.CreateSnapshot("retention-research"))
        {
            snapshotId = snapshot.SnapshotId;
        }
        database.Put([0x51], [0x71, 0x72, 0x73]);

        var captured = database.CaptureResearchRetentionSnapshot();

        var mainHistoryId = database.GetHistoryTopologyDiagnostics().Main.HistoryId;
        var main = Assert.Single(captured.Histories, history => history.HistoryId == mainHistoryId);
        Assert.Equal(2, main.Versions.Count(version => version.KeyBytes == 1));
        Assert.Contains(main.Versions, version => version.ValueBytes == 2 && !version.IsTombstone);
        Assert.Contains(main.Versions, version => version.ValueBytes == 3 && !version.IsTombstone);
        var root = Assert.Single(captured.PersistentRoots, item => item.RootId == snapshotId);
        Assert.Equal(mainHistoryId, root.ProtectedHistoryId);
        Assert.Empty(captured.ActiveBoundaries);

        var inspector = new RetentionInspector(captured);
        var explanation = inspector.ExplainRetention(snapshotId);
        Assert.NotEmpty(explanation.RequiredVersionIds);
    }

    private sealed class ThrowingResearchEventSink : IResearchEventSink
    {
        public ResearchTelemetryMode Mode => ResearchTelemetryMode.Trace;

        public void Publish(ResearchEvent researchEvent)
            => throw new InvalidOperationException("Test sink failure.");
    }
}
