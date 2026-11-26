using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Loading;

// A child publishes a complete transaction or retains its previous applied position
// (docs/decisions/replication-and-hosting.md). Preparing the next transaction must
// have no effect on the outcome or resource ownership of the current one.
public sealed class ReceivedScratchTests
{
    private const string EagerScratch = "Replacing oversized receive scratch can allocate after publication or rollback.";
    private const int KeptMembers = ReceivedScratchFixture.FirstOversizedMember - 1;
    private const int OversizedMembers = ReceivedScratchFixture.FirstOversizedMember;
    private const int FollowUpKey = 9_000;

    [Theory(Skip = EagerScratch)]
    [InlineData(KeptMembers)]
    [InlineData(OversizedMembers)]
    public void A_large_committed_replay_reports_its_own_success_and_keeps_or_discards_scratch_at_the_boundary(int members)
    {
        var allocator = new CountingNativeAllocator();
        try
        {
            using var arena = OpenArena(32, allocator);
            var receiver = new ReceivedScratchFixture(arena);
            Assert.True(allocator.Obtained > 0);
            var failedAllocation = new OutOfMemoryException("Scratch allocation was refused.");
            receiver.ScratchFailure = failedAllocation;

            var failure = Record.Exception(() => receiver.ApplyBatch(2, members));
            receiver.ExpectState(2, members);
            Assert.Null(failure);
            Assert.Equal(0, receiver.ScratchAllocations);

            var following = Record.Exception(() => receiver.ApplyOne(3, FollowUpKey));
            if (members == OversizedMembers)
            {
                Assert.Same(failedAllocation, following);
                Assert.Equal(1, receiver.ScratchAllocations);
                receiver.ExpectState(2, members);
                receiver.ApplyOne(3, FollowUpKey);
            }
            else
            {
                Assert.Null(following);
                Assert.Equal(0, receiver.ScratchAllocations);
            }

            receiver.ExpectState(3, members, FollowUpKey);
        }
        finally
        {
            allocator.ExpectEverythingReturned();
        }
    }

    [Fact(Skip = EagerScratch)]
    public void Rollback_reports_the_original_preparation_exception_and_the_next_attempt_can_reuse_its_keys()
    {
        var allocator = new CountingNativeAllocator();
        try
        {
            using var arena = OpenArena(32, allocator);
            var receiver = new ReceivedScratchFixture(arena);
            Assert.True(allocator.Obtained > 0);
            var initialLiveBytes = arena.Counters.LiveBytes;
            var failedPreparation = new OutOfMemoryException("The last full write could not be recorded.");
            var failedAllocation = new OutOfMemoryException("Scratch allocation was refused.");
            receiver.PreparationFailure = failedPreparation;
            receiver.ScratchFailure = failedAllocation;

            var failure = Record.Exception(() => receiver.ApplyBatch(2, OversizedMembers));
            receiver.ExpectState(1, members: 0);
            Assert.Equal(initialLiveBytes, arena.Counters.LiveBytes);
            Assert.Equal(1, receiver.LargePreparations);
            Assert.Same(failedPreparation, failure);
            Assert.Equal(0, receiver.ScratchAllocations);

            var nextFailure = Record.Exception(() => receiver.ApplyOne(2, key: 1));
            Assert.Same(failedAllocation, nextFailure);
            receiver.ExpectState(1, members: 0);

            receiver.ApplyOne(2, key: 1);
            receiver.ExpectState(2, members: 1);
        }
        finally
        {
            allocator.ExpectEverythingReturned();
        }
    }

    [Theory(Skip = EagerScratch)]
    [InlineData(false)]
    [InlineData(true)]
    public void A_native_refusal_returns_the_staged_roots_once_on_each_attempt_and_leaves_the_child_usable(bool refusesScratch)
    {
        // The seed and first 1,024 inserts fit. The final root is valid but its Binary
        // needs more regions than the arena has left.
        var allocator = new CountingNativeAllocator();
        try
        {
            using var arena = OpenArena(3, allocator);
            var receiver = new ReceivedScratchFixture(arena);
            Assert.True(allocator.Obtained > 0);
            var initialLiveBytes = arena.Counters.LiveBytes;
            var failedAllocation = new OutOfMemoryException("Scratch allocation was refused.");
            receiver.ScratchFailure = refusesScratch ? failedAllocation : null;

            var failure = Record.Exception(() => receiver.ApplyBatch(2, OversizedMembers, finalBinaryLength: 150_000));
            receiver.ExpectState(1, members: 0);
            Assert.Equal(initialLiveBytes, arena.Counters.LiveBytes);
            if (refusesScratch)
            {
                Assert.Equal(1, receiver.LargePreparations);
                Assert.Equal(1, receiver.ScratchAllocations);
                Assert.Same(failedAllocation, failure);
            }
            else
            {
                Assert.Equal(3, receiver.LargePreparations);
                var exhausted = Assert.IsType<LatticeException>(failure);
                Assert.Equal(LatticeErrorEnum.Unavailable, exhausted.Error);
                Assert.Contains("cannot hold the transaction", exhausted.Message, StringComparison.Ordinal);
            }

            receiver.ApplyOne(2, key: 1);
            receiver.ExpectState(2, members: 1);
        }
        finally
        {
            allocator.ExpectEverythingReturned();
        }
    }

    private static Arena OpenArena(int segments, CountingNativeAllocator allocator) => new(new ArenaOptions
    {
        SegmentSize = Arena.MaxBlockSize,
        MemoryLimit = (long)segments * Arena.MaxBlockSize,
        ReservedSegments = 0,
    }, allocator);
}
