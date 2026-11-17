using System.Collections.Concurrent;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Writes;

// Until S11 a master of the engine refuses a write with NotSupportedException while a child is connected
// or loading, and a loaded child stays at its readiness position (brief of Epic 1, sprint S10, "Out of
// scope"). Where that is decided (the planner's block of 17 November 2026, point 6): under the master's
// lock of the writes, which the registration of a child takes too, so a write is either published before
// the capture of the child starts, and then is in what the child loads, or refused; none is in between.
// - While a child is connected a write is refused, also one that would change nothing, which would take
//   a position of its own (the owner's answer to Q39); the master and the child stay where they were.
// - While a child is loading, held in the middle of its load by a link (HeldLink), a write is refused,
//   and the child that completes its load does not have it.
// - Writes of several callers that race the registration of a child are each either published, and
//   then on the child, or refused, and then on neither; the attempts begun after the child was ready
//   are refused.
// - Once the child has closed and the master has seen its connection end, the master publishes writes
//   again (AcceptedWrite).
public sealed class WritesWhileAChildIsRegisteredTests
{
    private const int Writers = 4;

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task Master_refuses_writes_with_NotSupportedException_while_a_child_is_connected_and_both_stay_at_the_readiness_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var readiness = child.Position;
        var pieces = master.Set<PieceSchema.Piece>();
        var records = master.Set("Piece");

        await Assert.ThrowsAsync<NotSupportedException>(async () => await pieces.InsertAsync(PieceSchema.Of(3, name: 8)));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await records.DeleteAsync(1L));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await records.DeleteAsync(404L));

        Assert.Equal(readiness, master.Position);
        Assert.Equal(readiness, child.Position);
        Assert.Null(pieces.Get(3L));
        Assert.NotNull(pieces.Get(1L));
        Pieces.ExpectSame(master, child);
    }

    [Fact]
    public async Task Master_refuses_writes_with_NotSupportedException_while_a_child_is_loading_and_the_child_loads_without_them()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        var before = master.Position;
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound));

        var opening = child.OpenAsync().AsTask();
        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(100, name: 8)));
        Assert.Equal(before, master.Position);

        link.Release();
        await opening.WaitAsync(Bound);

        Assert.Equal(before, child.Position);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(100L));
        Pieces.ExpectSame(master, child);
    }

    [Fact]
    public async Task Writes_that_race_the_registration_of_a_child_are_each_on_the_child_or_refused_and_none_is_in_between()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var published = new ConcurrentBag<long>();
        var refused = new ConcurrentBag<long>();
        var refusedAfterReady = new ConcurrentBag<long>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var started = new CountdownEvent(Writers);

        // Each writer inserts pieces of its own until an attempt it began after the child was ready has
        // ended; it signals once its first attempt has ended.
        var writers = Enumerable.Range(0, Writers)
            .Select(writer => Task.Run(async () =>
            {
                var pieces = master.Set<PieceSchema.Piece>();

                for (var attempt = 0L; ; attempt++)
                {
                    var id = (writer * 1_000_000_000L) + attempt;
                    var afterReady = ready.Task.IsCompleted;

                    try
                    {
                        await pieces.InsertAsync(PieceSchema.Of(id, name: 8));
                        published.Add(id);
                    }
                    catch (NotSupportedException)
                    {
                        refused.Add(id);

                        if (afterReady)
                        {
                            refusedAfterReady.Add(id);
                        }
                    }

                    if (attempt == 0)
                    {
                        started.Signal();
                    }

                    if (afterReady)
                    {
                        return;
                    }
                }
            }))
            .ToArray();

        Assert.True(started.Wait(Bound), "The writers did not start.");
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        ready.SetResult();
        await Task.WhenAll(writers).WaitAsync(Bound);

        var onMaster = master.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).ToHashSet();
        var onChild = child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).ToHashSet();

        Assert.True(onMaster.SetEquals(published), "The master does not hold exactly the writes that were published.");
        Assert.True(onChild.SetEquals(published), "The child does not hold exactly the writes that were published.");
        Assert.DoesNotContain(refused, onChild.Contains);
        Assert.Equal(Writers, refusedAfterReady.Count);
        Assert.Equal(master.Position, child.Position);
    }

    [Fact]
    public async Task Master_publishes_writes_again_once_its_child_has_closed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        await child.CloseAsync().AsTask().WaitAsync(Bound);
        await AcceptedWrite.UntilPublishedAsync(master, 3);

        Assert.NotNull(master.Set<PieceSchema.Piece>().Get(3L));
    }
}
