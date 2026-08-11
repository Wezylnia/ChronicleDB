using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.Diagnostics.Research;

public enum ObservedHistoryLifecycle : byte
{
    Unknown = 0,
    Active = 1,
    Deleting = 2,
    Deleted = 3,
}

public enum ObservedRootKind : byte
{
    Unknown = 0,
    PersistentSnapshot = 1,
    BranchBase = 2,
}

public enum ObservedRootLifecycle : byte
{
    Unknown = 0,
    Active = 1,
    Revoked = 2,
    Deleted = 3,
}

public enum ObservationAvailability : byte
{
    Discovered = 0,
    MetadataValidated = 1,
    DependencyClosureValidated = 2,
    AuthorityValidated = 3,
    Recovering = 4,
    Ready = 5,
    Unvalidated = 6,
    Unavailable = 7,
    Corrupt = 8,
}

public enum ObservationErrorKind : byte
{
    None = 0,
    Unavailable = 1,
    Corrupt = 2,
    InvalidTransition = 3,
    Unknown = 4,
}

/// <summary>
/// Immutable canonical key/value observation. A tombstone has an empty value.
/// </summary>
public sealed class ObservedEntry : IEquatable<ObservedEntry>
{
    private readonly byte[] _key;
    private readonly byte[] _value;

    public ObservedEntry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool isTombstone)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("An observed key cannot be empty.", nameof(key));
        }

        if (isTombstone && !value.IsEmpty)
        {
            throw new ArgumentException("A tombstone must not carry a value.", nameof(value));
        }

        _key = key.ToArray();
        _value = value.ToArray();
        IsTombstone = isTombstone;
    }

    public ReadOnlyMemory<byte> Key => _key;

    public ReadOnlyMemory<byte> Value => _value;

    public bool IsTombstone { get; }

    public bool Equals(ObservedEntry? other)
        => other is not null
            && IsTombstone == other.IsTombstone
            && _key.AsSpan().SequenceEqual(other._key)
            && _value.AsSpan().SequenceEqual(other._value);

    public override bool Equals(object? obj) => obj is ObservedEntry other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsTombstone);
        foreach (var value in _key)
        {
            hash.Add(value);
        }

        foreach (var value in _value)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

public sealed class LogicalDataObservation
{
    public LogicalDataObservation(
        HistoryId historyId,
        CommitSequence boundary,
        IEnumerable<ObservedEntry> entries)
    {
        if (!historyId.IsValid)
        {
            throw new ArgumentException("A logical observation requires a valid history ID.", nameof(historyId));
        }

        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries.OrderBy(entry => entry, ObservedEntryComparer.Instance).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Key.Span.SequenceEqual(ordered[index].Key.Span))
            {
                throw new ArgumentException("A logical observation cannot contain duplicate keys.", nameof(entries));
            }
        }

        HistoryId = historyId;
        Boundary = boundary;
        Entries = Array.AsReadOnly(ordered);
    }

    public HistoryId HistoryId { get; }

    public CommitSequence Boundary { get; }

    public IReadOnlyList<ObservedEntry> Entries { get; }

    private sealed class ObservedEntryComparer : IComparer<ObservedEntry>
    {
        public static ObservedEntryComparer Instance { get; } = new();

        public int Compare(ObservedEntry? left, ObservedEntry? right)
            => left is null
                ? right is null ? 0 : -1
                : right is null ? 1 : left.Key.Span.SequenceCompareTo(right.Key.Span);
    }
}

public sealed record HistoryTopologyObservation(
    HistoryId HistoryId,
    HistoryId? ParentHistoryId,
    CommitSequence? BaseBoundary,
    ObservedHistoryLifecycle Lifecycle);

public sealed record RootLifecycleObservation(
    HistoryRootId RootId,
    ObservedRootKind Kind,
    HistoryId OwnerHistoryId,
    HistoryId ProtectedHistoryId,
    CommitSequence Boundary,
    ObservedRootLifecycle Lifecycle);

public readonly record struct AuthorityObservation(
    ulong WalGeneration,
    ulong CheckpointGeneration,
    string PublishedAuthority);

public readonly record struct SequenceObservation(
    HistoryId HistoryId,
    CommitSequence CommittedSequence,
    CommitSequence RetentionFloor);

public readonly record struct AvailabilityObservation(ObservationAvailability State);

public readonly record struct ErrorObservation(
    ObservationErrorKind Kind,
    string? Code);

public readonly record struct CorruptionObservation(
    bool Detected,
    string? Code);

public readonly record struct SafetyPredicateObservation(
    bool NoPhantomCommit,
    bool NoCrossHistoryReplay,
    bool BaseStable,
    bool NoInvalidRoot,
    bool NoPrematureReclaim,
    bool NoEarlyPublication);

/// <summary>
/// Canonical, property-relevant observation used by research oracles.
/// It is observational only and never becomes durability or retention authority.
/// </summary>
public sealed class ObservationEnvelope
{
    public ObservationEnvelope(
        LogicalDataObservation? logicalData,
        IEnumerable<HistoryTopologyObservation> historyTopology,
        IEnumerable<RootLifecycleObservation> rootLifecycle,
        AuthorityObservation authority,
        IEnumerable<SequenceObservation> sequences,
        AvailabilityObservation availability,
        ErrorObservation error,
        CorruptionObservation corruption,
        SafetyPredicateObservation safetyPredicates)
    {
        ArgumentNullException.ThrowIfNull(historyTopology);
        ArgumentNullException.ThrowIfNull(rootLifecycle);
        ArgumentNullException.ThrowIfNull(sequences);

        LogicalData = logicalData;
        HistoryTopology = Array.AsReadOnly(
            historyTopology.OrderBy(item => item.HistoryId.Value).ToArray());
        RootLifecycle = Array.AsReadOnly(
            rootLifecycle.OrderBy(item => item.RootId.Value).ToArray());
        Authority = authority;
        Sequences = Array.AsReadOnly(
            sequences.OrderBy(item => item.HistoryId.Value).ToArray());
        Availability = availability;
        Error = error;
        Corruption = corruption;
        SafetyPredicates = safetyPredicates;
    }

    public LogicalDataObservation? LogicalData { get; }

    public IReadOnlyList<HistoryTopologyObservation> HistoryTopology { get; }

    public IReadOnlyList<RootLifecycleObservation> RootLifecycle { get; }

    public AuthorityObservation Authority { get; }

    public IReadOnlyList<SequenceObservation> Sequences { get; }

    public AvailabilityObservation Availability { get; }

    public ErrorObservation Error { get; }

    public CorruptionObservation Corruption { get; }

    public SafetyPredicateObservation SafetyPredicates { get; }
}
