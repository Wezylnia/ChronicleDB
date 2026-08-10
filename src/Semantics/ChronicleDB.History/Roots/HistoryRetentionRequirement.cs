using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;

namespace ChronicleDB.History.Roots;

/// <summary>
/// Explainable retention requirement consumed by reclamation and diagnostics.
/// </summary>
public sealed record HistoryRetentionRequirement(
    HistoryRootId RootId,
    HistoryRootKind Kind,
    HistoryId HistoryId,
    CommitSequence Boundary,
    HistoryRootState State);
