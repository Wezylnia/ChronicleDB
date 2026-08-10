using ChronicleDB;
using ChronicleDB.Transactions.Faults;

namespace ChronicleDB.RecoveryTests;

public sealed class FaultInjectionTests
{
    [Fact]
    public void CommitInvokesFaultPointsInProtocolOrder()
    {
        using var directory = new StorageTestDirectory();
        var injector = new RecordingInjector();
        using var database = ChronicleDatabase.Open(directory.Path, faultInjector: injector);
        using var transaction = database.BeginTransaction();
        transaction.Put([1], [2]);
        transaction.Commit();

        Assert.Equal(
            [
                TransactionFaultPoint.BeforeWalAppend,
                TransactionFaultPoint.AfterWalAppend,
                TransactionFaultPoint.BeforeWalFlush,
                TransactionFaultPoint.AfterWalFlush,
                TransactionFaultPoint.BeforePhysicalPublication,
                TransactionFaultPoint.AfterPhysicalPublication,
                TransactionFaultPoint.BeforeAcknowledgement
            ],
            injector.Points);
    }

    private sealed class RecordingInjector : ITransactionFaultInjector
    {
        public List<TransactionFaultPoint> Points { get; } = [];

        public void Hit(TransactionFaultPoint point) => Points.Add(point);
    }
}
