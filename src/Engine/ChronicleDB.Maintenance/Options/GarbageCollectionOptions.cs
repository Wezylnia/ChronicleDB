namespace ChronicleDB.Maintenance;

/// <summary>
/// Conservative v0.9 historical-reclamation policy. The engine keeps at least
/// <see cref="RetainRecentCommits"/> generic point-in-time commit boundaries per
/// independently writable history in addition to every explicit persistent or
/// process-local retention root.
/// </summary>
public sealed class GarbageCollectionOptions
{
    public int RetainRecentCommits { get; init; } = 1024;

    public bool IncludeBranches { get; init; } = true;

    public void Validate()
    {
        if (RetainRecentCommits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetainRecentCommits),
                "Retained commit count cannot be negative.");
        }
    }
}
