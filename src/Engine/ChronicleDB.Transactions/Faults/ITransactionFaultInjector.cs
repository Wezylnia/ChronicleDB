namespace ChronicleDB.Transactions.Faults;

public interface ITransactionFaultInjector
{
    void Hit(TransactionFaultPoint point);
}
