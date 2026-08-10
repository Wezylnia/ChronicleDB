using ChronicleDB.Core.Identifiers;
using ChronicleDB.Core.Sequences;
using ChronicleDB.Transactions;
using ChronicleDB.Transactions.State;

namespace ChronicleDB.UnitTests.Transactions;

public sealed class TransactionTests
{
    [Fact]
    public void StateMachineFollowsCommitPathAndReleasesPrivateWrites()
    {
        var transaction = new Transaction(startSequence: new CommitSequence(17));
        Assert.Equal(TransactionState.Created, transaction.State);
        Assert.Equal((ulong)17, transaction.StartSequence.Value);

        transaction.Begin();
        transaction.Put([1], [2]);
        transaction.Prepare();
        transaction.MarkCommitting();
        transaction.MarkDurableCommitted();
        transaction.MarkCommitted();

        Assert.Equal(TransactionState.Committed, transaction.State);
        Assert.Equal(0, transaction.WriteCount);
        Assert.Throws<InvalidOperationException>(() => transaction.Put([1], [3]));
    }

    [Fact]
    public void AbortClearsWritesAndIsTerminal()
    {
        var transaction = new Transaction();
        transaction.Begin();
        transaction.Put([9], [8]);
        transaction.Delete([10]);

        transaction.Abort();

        Assert.Equal(TransactionState.Aborted, transaction.State);
        Assert.Equal(0, transaction.WriteCount);
        Assert.Throws<InvalidOperationException>(() => transaction.Abort());
        Assert.Throws<InvalidOperationException>(() => transaction.GetWriteSet());
    }

    [Fact]
    public void LocalWritesUseFullKeysAndDoNotAliasBuffers()
    {
        var transaction = new Transaction(new TransactionId(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        transaction.Begin();
        var key = new byte[] { 0, 255 };
        var value = new byte[] { 1, 2, 3 };
        transaction.Put(key, value);
        key[0] = 7;
        value[0] = 7;

        Assert.True(transaction.TryGetLocal([0, 255], out var stored));
        Assert.Equal(new byte[] { 1, 2, 3 }, stored);
        stored[1] = 99;
        Assert.True(transaction.TryGetLocal([0, 255], out var reread));
        Assert.Equal(new byte[] { 1, 2, 3 }, reread);
        Assert.False(transaction.TryGetLocal([0, 254], out _));
    }

    [Fact]
    public void RepeatedMutationsKeepOnlyTheNewestWritePerKey()
    {
        var transaction = new Transaction();
        transaction.Begin();
        transaction.Put([1], [1]);
        transaction.Put([1], [2]);
        transaction.Delete([1]);
        transaction.Put([2], [3]);

        var writes = transaction.GetWriteSet();
        Assert.Equal(2, writes.Count);
        Assert.Contains(writes, write => write.Key.Equals(new ChronicleDB.Core.Keys.BinaryKey([1])) && write.IsDelete);
        Assert.Contains(writes, write => write.Key.Equals(new ChronicleDB.Core.Keys.BinaryKey([2])) && !write.IsDelete);
    }

    [Fact]
    public void InvalidStateTransitionsFailDeterministically()
    {
        var transaction = new Transaction();
        Assert.Throws<InvalidOperationException>(() => transaction.Put([1], [1]));
        Assert.Throws<InvalidOperationException>(() => transaction.Prepare());
        Assert.Throws<InvalidOperationException>(() => transaction.MarkCommitting());
        Assert.Throws<InvalidOperationException>(() => transaction.MarkCommitted());

        transaction.Begin();
        transaction.Prepare();
        Assert.Throws<InvalidOperationException>(() => transaction.Begin());
        Assert.Throws<InvalidOperationException>(() => transaction.MarkCommitted());
    }

    [Fact]
    public void DurableCommitCannotBeAbortedAndMustBePublishedOrRecovered()
    {
        var transaction = new Transaction();
        transaction.Begin();
        transaction.Put([1], [2]);
        transaction.Prepare();
        transaction.MarkCommitting();
        transaction.MarkDurableCommitted();

        Assert.Equal(TransactionState.DurableCommitted, transaction.State);
        Assert.Throws<InvalidOperationException>(() => transaction.Abort());

        transaction.MarkCommitted();
        Assert.Equal(TransactionState.Committed, transaction.State);
    }
}
