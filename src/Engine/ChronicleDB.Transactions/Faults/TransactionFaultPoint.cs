namespace ChronicleDB.Transactions.Faults;

public enum TransactionFaultPoint
{
    BeforeWalAppend = 1,
    AfterWalAppend = 2,
    BeforeWalFlush = 3,
    AfterWalFlush = 4,
    BeforePhysicalPublication = 5,
    AfterPhysicalPublication = 6,
    BeforeAcknowledgement = 7
}
