namespace ChronicleDB.Indexing;

public readonly record struct VersionIndexStatistics(
    long Lookups,
    long Publications,
    long Removals,
    long ContendedAcquisitions);
