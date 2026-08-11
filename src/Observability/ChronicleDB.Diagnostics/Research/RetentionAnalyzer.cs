namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// One logical version in a retention oracle. Version IDs must be stable within
/// one experiment; they are not persistence authority.
/// </summary>
public sealed record RetentionVersion
{
    public RetentionVersion(
        string versionId,
        long logicalPayloadBytes,
        long serializedBytes,
        bool isTombstone = false)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new ArgumentException("A retention version requires a stable ID.", nameof(versionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(logicalPayloadBytes);

        if (serializedBytes < logicalPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serializedBytes),
                "Serialized bytes cannot be smaller than logical payload bytes.");
        }

        VersionId = versionId;
        LogicalPayloadBytes = logicalPayloadBytes;
        SerializedBytes = serializedBytes;
        IsTombstone = isTombstone;
    }

    public string VersionId { get; }

    public long LogicalPayloadBytes { get; }

    public long SerializedBytes { get; }

    public bool IsTombstone { get; }
}

public sealed class RetentionRoot
{
    public RetentionRoot(string rootId, IEnumerable<RetentionVersion> requiredVersions)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            throw new ArgumentException("A retention root requires a stable ID.", nameof(rootId));
        }

        ArgumentNullException.ThrowIfNull(requiredVersions);
        var versions = requiredVersions.ToArray();
        if (versions.GroupBy(version => version.VersionId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("A retention root cannot list one version twice.", nameof(requiredVersions));
        }

        RootId = rootId;
        RequiredVersions = Array.AsReadOnly(versions);
    }

    public string RootId { get; }

    public IReadOnlyList<RetentionVersion> RequiredVersions { get; }
}

public sealed class RetentionContext
{
    public RetentionContext(
        IEnumerable<RetentionVersion> globallyRequiredVersions,
        IEnumerable<RetentionRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(globallyRequiredVersions);
        ArgumentNullException.ThrowIfNull(roots);

        GloballyRequiredVersions = Array.AsReadOnly(globallyRequiredVersions.ToArray());
        var rootArray = roots.ToArray();
        if (rootArray.GroupBy(root => root.RootId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Retention root IDs must be unique.", nameof(roots));
        }

        Roots = Array.AsReadOnly(rootArray);
    }

    public IReadOnlyList<RetentionVersion> GloballyRequiredVersions { get; }

    public IReadOnlyList<RetentionRoot> Roots { get; }
}

public sealed record RetentionAnalysisResult(
    int ProtectedVersionCount,
    int ProtectedVersionCountAfterDrop,
    long CurrentLivePayloadBytes,
    long LivePayloadBytesAfterDrop,
    long MarginalPayloadBytes,
    long CurrentSerializedBytes,
    long SerializedBytesAfterDrop,
    long MarginalSerializedBytes,
    int UniqueRequiredVersionCount,
    int SharedRequiredVersionCount,
    long UniqueProtectedPayloadBytes,
    long SharedProtectedPayloadBytes);

/// <summary>
/// Independent set-level retention accounting for P1. It computes marginal debt
/// from a root context; it does not decide whether the engine may reclaim anything.
/// </summary>
public static class MarginalRetentionAnalyzer
{
    public static RetentionAnalysisResult Analyze(
        RetentionContext context,
        IEnumerable<string> rootSetToDrop)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rootSetToDrop);

        var selectedRootIds = rootSetToDrop
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedRootIds.Length == 0)
        {
            throw new ArgumentException("At least one root must be selected.", nameof(rootSetToDrop));
        }

        var rootsById = context.Roots.ToDictionary(root => root.RootId, StringComparer.Ordinal);
        if (selectedRootIds.Any(rootId => !rootsById.ContainsKey(rootId)))
        {
            throw new ArgumentException("The selected root set contains an unknown root.", nameof(rootSetToDrop));
        }

        var allRequired = CollectVersions(
            context.GloballyRequiredVersions,
            context.Roots.SelectMany(root => root.RequiredVersions));
        var remainingRoots = context.Roots
            .Where(root => !selectedRootIds.Contains(root.RootId, StringComparer.Ordinal))
            .SelectMany(root => root.RequiredVersions);
        var afterDrop = CollectVersions(context.GloballyRequiredVersions, remainingRoots);
        var selected = CollectVersions(
            selectedRootIds.SelectMany(rootId => rootsById[rootId].RequiredVersions));
        var shared = selected.Keys.Intersect(afterDrop.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var unique = selected.Keys.Except(afterDrop.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        return new RetentionAnalysisResult(
            ProtectedVersionCount: allRequired.Count,
            ProtectedVersionCountAfterDrop: afterDrop.Count,
            CurrentLivePayloadBytes: Sum(allRequired.Values, version => version.LogicalPayloadBytes),
            LivePayloadBytesAfterDrop: Sum(afterDrop.Values, version => version.LogicalPayloadBytes),
            MarginalPayloadBytes: Difference(allRequired, afterDrop, version => version.LogicalPayloadBytes),
            CurrentSerializedBytes: Sum(allRequired.Values, version => version.SerializedBytes),
            SerializedBytesAfterDrop: Sum(afterDrop.Values, version => version.SerializedBytes),
            MarginalSerializedBytes: Difference(allRequired, afterDrop, version => version.SerializedBytes),
            UniqueRequiredVersionCount: unique.Count,
            SharedRequiredVersionCount: shared.Count,
            UniqueProtectedPayloadBytes: Sum(unique.Select(versionId => selected[versionId]), version => version.LogicalPayloadBytes),
            SharedProtectedPayloadBytes: Sum(shared.Select(versionId => selected[versionId]), version => version.LogicalPayloadBytes));
    }

    private static Dictionary<string, RetentionVersion> CollectVersions(
        params IEnumerable<RetentionVersion>[] sources)
    {
        var versions = new Dictionary<string, RetentionVersion>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var version in source)
            {
                if (versions.TryGetValue(version.VersionId, out var existing)
                    && (existing.LogicalPayloadBytes != version.LogicalPayloadBytes
                        || existing.SerializedBytes != version.SerializedBytes
                        || existing.IsTombstone != version.IsTombstone))
                {
                    throw new ArgumentException(
                        $"Version '{version.VersionId}' has conflicting size or tombstone metadata.");
                }

                versions[version.VersionId] = version;
            }
        }

        return versions;
    }

    private static long Difference(
        IReadOnlyDictionary<string, RetentionVersion> all,
        IReadOnlyDictionary<string, RetentionVersion> afterDrop,
        Func<RetentionVersion, long> selector)
        => checked(Sum(all.Values, selector) - Sum(afterDrop.Values, selector));

    private static long Sum(IEnumerable<RetentionVersion> versions, Func<RetentionVersion, long> selector)
        => checked(versions.Sum(selector));
}
