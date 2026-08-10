using ChronicleDB.Core.Sequences;
using ChronicleDB.Mvcc.Versions;

namespace ChronicleDB.Mvcc.Visibility;

/// <summary>
/// The authoritative baseline rule for committed-version visibility.
/// Transaction-local writes are resolved before this rule is evaluated.
/// </summary>
public static class VersionVisibility
{
    public static bool IsVisible(
        VersionMetadata version,
        CommitSequence visibilityBoundary)
        => version.State == VersionState.Committed
           && version.CommitSequence <= visibilityBoundary;
}
