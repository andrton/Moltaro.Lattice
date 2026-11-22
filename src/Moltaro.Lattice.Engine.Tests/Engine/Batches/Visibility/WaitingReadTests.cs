using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Visibility;

// A read that meets a member of a batch whose position is not published yet waits for the position, inside
// the protection of reads and outside every lock (exception X6, the wait of a read for a step of a
// publication), and only such a read waits; how each wait ends (the brief of Epic 1, sprint S17, the tests
// of point 2):
// - when the publication goes on, the read returns the state after the batch, at its position or later;
// - when the publication fails, a read that waits gives up with Unavailable, as the instance is Faulted,
//   and the commit of the batch ends in IndeterminateOutcome;
// - an asynchronous read ends with its token, OperationCanceledException, while the publication is still
//   held, and the database goes on afterwards.
// The publication of the batch of BatchSchema is held after the stores of every member and before the
// position (HeldPublication), on the master that publishes it or on a child that applies it; the read
// switch tells that the read has found the root of its member.
public sealed class WaitingReadTests
{
    public static IEnumerable<TheoryDataRow<string>> Nodes => [new("master"), new("child")];

    [Theory(Skip = SkipReason.WriteBatch)]
    [MemberData(nameof(Nodes))]
    public async Task Read_that_meets_a_member_while_the_publication_is_held_waits_and_then_returns_the_state_after_the_batch(string node)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var items = nodes.Reader.Set<Item>();
        var s = nodes.NextSequence;

        using var held = HeldPublication.Arm(nodes.Reader, PublicationPointEnum.BeforePosition);
        var commit = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync(nodes.Session)));

        await held.ReachedAsync();

        var read = await StartAtTheRootAsync(nodes.Reader, () => OwnThread.Wait(items.GetAsync(1L, new LatticeSession())));

        Assert.False(read.IsCompleted, "A read of a member returned while the position of its batch was not published.");
        Assert.Equal(("old", 1L), StateOf(items.Get(Other)));

        held.Release();

        var answer = await read.WaitAsync(Bound);

        Assert.Equal(After(1), StateOf(answer.Entity));
        Assert.True(PositionSequence.Of(answer.Position) >= s);
        await commit.WaitAsync(Bound);
    }

    [Theory(Skip = SkipReason.WriteBatch)]
    [MemberData(nameof(Nodes))]
    public async Task Read_that_waits_for_a_publication_that_fails_ends_with_Unavailable(string node)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var items = nodes.Reader.Set<Item>();

        using var held = HeldPublication.Arm(nodes.Reader, PublicationPointEnum.BeforePosition);
        var commit = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync(nodes.Session)));

        await held.ReachedAsync();

        var byKey = await StartAtTheRootAsync(nodes.Reader, () => items.Get(2L));
        var search = await StartAtTheRootAsync(nodes.Reader, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions())));

        held.Fail();

        Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => byKey.WaitAsync(Bound))).Error);
        Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => search.WaitAsync(Bound))).Error);
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, nodes.Reader.State);

        if (node == "master")
        {
            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, (await Assert.ThrowsAsync<LatticeException>(() => commit.WaitAsync(Bound))).Error);
        }
    }

    [Theory(Skip = SkipReason.WriteBatch)]
    [MemberData(nameof(Nodes))]
    public async Task Asynchronous_read_that_waits_for_the_position_of_a_batch_ends_with_its_token(string node)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var items = nodes.Reader.Set<Item>();

        using var held = HeldPublication.Arm(nodes.Reader, PublicationPointEnum.BeforePosition);
        var commit = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync(nodes.Session)));

        await held.ReachedAsync();

        using var cancellation = new CancellationTokenSource();
        var byKey = await StartAtTheRootAsync(nodes.Reader, () => OwnThread.Wait(items.GetAsync(1L, new LatticeSession(), cancellation.Token)));
        var search = await StartAtTheRootAsync(nodes.Reader, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions(), cancellation.Token)));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => byKey.WaitAsync(Bound));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(Bound));

        held.Release();
        await commit.WaitAsync(Bound);
        await nodes.Reader.WaitForPositionAsync(nodes.Session).AsTask().WaitAsync(Bound);

        Assert.Equal(After(1), StateOf(items.Get(1L)));
    }

    // Starts the read on a thread of its own and returns once it has found the root of its member inside
    // the engine (the read switch), within the bound.
    private static async Task<Task<T>> StartAtTheRootAsync<T>(EngineDatabase database, Func<T> read)
    {
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        database.ArmReadSwitch(_ => inside.TrySetResult());

        var started = OwnThread.Run(read);

        await inside.Task.WaitAsync(Bound);

        return started;
    }
}
