using ChronicleDB.Core.Keys;

namespace ChronicleDB.Indexing;

public interface IVersionIndex
{
    int Count { get; }

    bool TryGet(BinaryKey key, out VersionHandle head);

    void Publish(BinaryKey key, VersionHandle head);

    bool TryRemove(BinaryKey key, out VersionHandle head);

    VersionIndexStatistics GetStatistics();
}
