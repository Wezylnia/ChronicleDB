using ChronicleDB.Core.Identifiers;

namespace ChronicleDB.Storage.Pages;

public readonly record struct PageHeader(
    PageId PageId,
    PageType Type,
    ulong Generation,
    ushort PayloadLength)
{
    public const int Size = 32;
}
