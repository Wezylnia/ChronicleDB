using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Storage.Faults;

public interface IStorageFaultInjector
{
    void Hit(StorageFaultPoint point, PageId pageId);
}
