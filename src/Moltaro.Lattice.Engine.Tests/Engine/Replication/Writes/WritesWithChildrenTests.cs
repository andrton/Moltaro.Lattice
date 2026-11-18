using System.Collections.Concurrent;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Writes;

// From Epic 1, sprint S11 a master of the engine accepts writes while children are connected or loading
// (brief of S11, the planner's block of 15 November 2026; decided after the study, point 2: no write is
// refused because of a child). This restates the tests of S10 that stated the refusal, which ends here:
// - while a child is connected, every write is published, also a delete of an absent key, which takes a
//   position of its own (the owner's answer to Q39), and the child applies each of them;
// - while a child is loading, held in the middle of its load by a link (HeldLink), a write is published,
//   and the child that completes its load has it;
// - writes of several callers that race the registration of a child — the start of its capture, S, taken
//   in a hold of the lock of the writes — are all published, and the child ends with every one of them:
//   a write published before S is in what the child copies, one published after S in the tail it is
//   sent, and none falls between the two.
// What a master holds for a child that has gone is stated by the tests of Delivery.
public sealed class WritesWithChildrenTests
{
    private const int Writers = 4;

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task Master_publishes_every_write_while_a_child_is_connected_and_the_child_applies_each()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var readiness = child.Position;
        var pieces = master.Set<PieceSchema.Piece>();
        var records = master.Set("Piece");

        var inserted = await pieces.InsertAsync(PieceSchema.Of(3, name: 8));
        var deleted = await records.DeleteAsync(1L);
        var deletedNothing = await records.DeleteAsync(404L);

        Assert.True(deleted.Changed);
        Assert.False(deletedNothing.Changed);
        Assert.Equal(PositionSequence.Of(readiness) + 1, PositionSequence.Of(inserted.Position));
        Assert.Equal(PositionSequence.Of(readiness) + 2, PositionSequence.Of(deleted.Position));
        Assert.Equal(PositionSequence.Of(readiness) + 3, PositionSequence.Of(deletedNothing.Position));

        await Eventually.AtAsync(child, master.Position);
        Pieces.ExpectSameAt(master, child);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(1L));
        Assert.NotNull(child.Set<PieceSchema.Piece>().Get(3L));
    }

    [Fact]
    public async Task Master_publishes_a_write_while_a_child_is_loading_and_the_child_has_it_once_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        var before = master.Position;
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound));

        var opening = child.OpenAsync().AsTask();
        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        var written = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(100, name: 8)).AsTask().WaitAsync(Bound);
        Assert.NotEqual(before, written.Position);

        link.Release();
        await opening.WaitAsync(Bound);

        Assert.NotNull(child.Set<PieceSchema.Piece>().Get(100L));
        Pieces.ExpectSameAt(master, child);
    }

    [Fact]
    public async Task Writes_that_race_the_registration_of_a_child_are_all_published_and_all_reach_the_child()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var published = new ConcurrentBag<long>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Enumerable.Range(0, Writers).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        // Each writer inserts pieces of its own until an insert it began after the child was ready has
        // ended; it signals once its first insert has ended. An insert that fails fails the writer. Each
        // writer runs on a thread of its own (OwnThread): an insert of a MemoryOnly master completes at
        // once, so a writer never gives its thread back while the child opens.
        var writers = Enumerable.Range(0, Writers)
            .Select(writer => OwnThread.Run(() =>
            {
                var pieces = master.Set<PieceSchema.Piece>();

                for (var attempt = 0L; ; attempt++)
                {
                    var id = (writer * 1_000_000_000L) + attempt;
                    var afterReady = ready.Task.IsCompleted;

                    OwnThread.Wait(pieces.InsertAsync(PieceSchema.Of(id, name: 8)));
                    published.Add(id);

                    if (attempt == 0)
                    {
                        started[writer].TrySetResult();
                    }

                    if (afterReady)
                    {
                        return;
                    }
                }
            }))
            .ToArray();

        // Every writer has ended its first insert before the child opens; a writer that failed its first
        // insert has ended, and the test reports it below.
        await Task.WhenAll(writers.Select((running, index) => Task.WhenAny(running, started[index].Task))).WaitAsync(Bound);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        ready.SetResult();
        await Task.WhenAll(writers).WaitAsync(Bound);

        await Eventually.AtAsync(child, master.Position);

        var onMaster = master.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).ToHashSet();
        var onChild = child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).ToHashSet();

        Assert.True(onMaster.SetEquals(published), "The master does not hold exactly the writes that were published.");
        Assert.True(onChild.SetEquals(published), "The child does not hold exactly the writes that were published.");
        Pieces.ExpectSameAt(master, child);
    }
}
