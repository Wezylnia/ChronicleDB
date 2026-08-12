using ChronicleDB.Diagnostics.Research;

namespace ChronicleDB.UnitTests.Research;

public sealed class ObserverScopedErasureAuthorityStoreTests
{
    [Fact]
    public void PublishedAuthorityRoundTripsWithSameCanonicalScopeHash()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = BuildDescriptor();

        var path = ObserverScopedErasureAuthorityStore.Publish(directory.Path, descriptor);
        var loaded = ObserverScopedErasureAuthorityStore.Load(directory.Path);

        Assert.True(File.Exists(path));
        Assert.Equal(descriptor.FormatVersion, loaded.FormatVersion);
        Assert.Equal(descriptor.KeyId, loaded.KeyId);
        Assert.Equal(descriptor.CanonicalSha256, loaded.CanonicalSha256);
        Assert.Equal(descriptor.Revocations.Count, loaded.Revocations.Count);
        Assert.Equal(descriptor.VisibilityRegions.Count, loaded.VisibilityRegions.Count);
        Assert.Equal(descriptor.RewriteRepresentationIds.Count, loaded.RewriteRepresentationIds.Count);
        Assert.Equal(descriptor.ReclaimRepresentationIds.Count, loaded.ReclaimRepresentationIds.Count);
        Assert.Equal(descriptor.VisibilityRegions[0].HistoryId, loaded.VisibilityRegions[0].HistoryId);
        Assert.Equal(descriptor.VisibilityRegions[0].MinimumBoundary, loaded.VisibilityRegions[0].MinimumBoundary);
        Assert.Equal(descriptor.VisibilityRegions[0].MaximumBoundary, loaded.VisibilityRegions[0].MaximumBoundary);
    }

    [Fact]
    public void TruncatedPublishedAuthorityFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        _ = ObserverScopedErasureAuthorityStore.Publish(directory.Path, BuildDescriptor());
        var path = Path.Combine(directory.Path, ObserverScopedErasureAuthorityStore.FileName);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..Math.Max(1, bytes.Length / 2)]);

        Assert.Throws<InvalidDataException>(() => { _ = ObserverScopedErasureAuthorityStore.Load(directory.Path); });
    }

    [Fact]
    public void SemanticallyMutatedPublishedAuthorityFailsCanonicalHashValidation()
    {
        using var directory = new TemporaryDirectory();
        _ = ObserverScopedErasureAuthorityStore.Publish(directory.Path, BuildDescriptor());
        var path = Path.Combine(directory.Path, ObserverScopedErasureAuthorityStore.FileName);
        var text = File.ReadAllText(path);
        text = text.Replace("\"keyId\": \"K\"", "\"keyId\": \"X\"", StringComparison.Ordinal);
        File.WriteAllText(path, text);

        Assert.Throws<InvalidDataException>(() => { _ = ObserverScopedErasureAuthorityStore.Load(directory.Path); });
    }

    [Fact]
    public void FaultBeforePublicationLeavesNoAuthoritativeFile()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<InjectedFault>(() => { _ = ObserverScopedErasureAuthorityStore.Publish(
            directory.Path,
            BuildDescriptor(),
            point =>
            {
                if (point == ObserverScopedErasureAuthorityFaultPoint.AfterFlushBeforePublish)
                {
                    throw new InjectedFault();
                }
            }); });

        Assert.Null(ObserverScopedErasureAuthorityStore.TryLoad(directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.creating"));
    }

    [Fact]
    public void FaultAfterPublicationLeavesValidatedAuthorityAuthoritative()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = BuildDescriptor();

        Assert.Throws<InjectedFault>(() => { _ = ObserverScopedErasureAuthorityStore.Publish(
            directory.Path,
            descriptor,
            point =>
            {
                if (point == ObserverScopedErasureAuthorityFaultPoint.AfterPublish)
                {
                    throw new InjectedFault();
                }
            }); });

        var loaded = ObserverScopedErasureAuthorityStore.Load(directory.Path);
        Assert.Equal(descriptor.CanonicalSha256, loaded.CanonicalSha256);
    }

    [Fact]
    public void OrphanCreatingFileIsNeverTreatedAsPublishedAuthority()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, ObserverScopedErasureAuthorityStore.FileName + ".orphan.creating"),
            "partial");

        Assert.Null(ObserverScopedErasureAuthorityStore.TryLoad(directory.Path));
    }

    [Fact]
    public void PublishedAuthorityIsImmutableWithinResearchDirectory()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = BuildDescriptor();
        _ = ObserverScopedErasureAuthorityStore.Publish(directory.Path, descriptor);

        Assert.Throws<IOException>(() =>
        {
            _ = ObserverScopedErasureAuthorityStore.Publish(directory.Path, descriptor);
        });
    }

    private static ObserverScopedErasureAuthorityDescriptor BuildDescriptor()
    {
        var history = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var snapshotId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
        var retention = new ResearchRetentionSnapshot(
            [new ResearchHistoryRetentionSnapshot(
                history,
                1,
                2,
                [
                    new ResearchCommittedVersionSnapshot("v1", Guid.Parse("00000000-0000-0000-0000-0000000000a3"), 1, "K", 8, 32, false),
                    new ResearchCommittedVersionSnapshot("v2", Guid.Parse("00000000-0000-0000-0000-0000000000a4"), 2, "K", 8, 0, true),
                ])],
            [new ResearchPersistentRetentionRootSnapshot(snapshotId, "PersistentSnapshot", history, history, 1)],
            []);
        var closure = new ErasureClosureInput(
            "K",
            history,
            [new ErasureHistoryNode(history, null)],
            [new ErasureRepresentation("wal", ErasureRepresentationKind.WalMutation, history, history, 1, ErasureContentState.Value, false)],
            PhysicalRepresentationScanComplete: true,
            []);
        var plan = ObserverExactErasureContractPlanner.Plan(retention, closure, ErasureMode.Force, forceAuthorized: true);
        return ObserverScopedErasureAuthorityDescriptorCompiler.Compile(plan);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronicle-a8-osea-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class InjectedFault : Exception;
}
