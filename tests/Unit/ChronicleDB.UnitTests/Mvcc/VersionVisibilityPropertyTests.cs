using ChronicleDB.Core.Sequences;
using ChronicleDB.Mvcc.Versions;
using ChronicleDB.Mvcc.Visibility;

namespace ChronicleDB.UnitTests.Mvcc;

public sealed class VersionVisibilityPropertyTests
{
    [Fact]
    public void TwoMillionDeterministicVisibilityCasesMatchAuthoritativeRule()
    {
        const int cases = 2_000_000;
        for (var index = 0; index < cases; index++)
        {
            var sequence = new CommitSequence(checked((ulong)(index % 10_000 + 1)));
            var boundary = new CommitSequence(checked((ulong)(index * 37L % 10_500)));
            var stateSelector = index % 3;
            var metadata = stateSelector switch
            {
                0 => VersionMetadata.Committed(sequence, isTombstone: (index & 1) != 0),
                1 => VersionMetadata.Pending(isTombstone: (index & 1) != 0),
                _ => VersionMetadata.Aborted(isTombstone: (index & 1) != 0)
            };
            var expected = stateSelector == 0 && sequence <= boundary;

            Assert.Equal(expected, VersionVisibility.IsVisible(metadata, boundary));
        }
    }
}
