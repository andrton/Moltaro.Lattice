using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class ViewMemoryTests
{
    private const int Mebibyte = 1024 * 1024;
    private const int MasterValue = 3 * Mebibyte;
    private const int ChildValue = 5 * Mebibyte / 2;

    private static readonly int Segment = new ArenaOptions().SegmentSize;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_write_waits_for_memory_held_by_a_view_then_succeeds_or_is_refused_by_the_budget(bool fits)
    {
        var arena = new ArenaOptions { MemoryLimit = 2L * Segment + Mebibyte };
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, arena, new NativeAllocator());
        var pieces = database.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: MasterValue));
        var recordSet = database.Set("Piece");
        using var pause = new ViewerPause();
        var reading = ReadPiece(1L, recordSet, pause);
        await pause.EnteredAsync(reading);

        var changed = await OwnThread.Run(() => OwnThread.Wait(pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: MasterValue)))).WaitAsync(Bound);
        Assert.True(database.Counters.Arena.RetiredBytes > 0);

        var wait = RefusedWriteWait.Begins(database);
        var attempt = PieceSchema.Of(1, name: 12, data: fits ? MasterValue : 2 * MasterValue);
        var writing = OwnThread.Run(() => OwnThread.Wait(pieces.ReplaceAsync(attempt)));
        Assert.Same(wait, await Task.WhenAny(wait, writing).WaitAsync(Bound));
        Assert.False(writing.IsCompleted);
        Assert.Equal(changed.Position, database.Position);

        pause.Release();

        Assert.Equal((10, MasterValue), await reading.WaitAsync(Bound));
        if (fits)
        {
            Assert.True((await writing.WaitAsync(Bound)).Changed);
            Assert.Equal(12, pieces.Get(1L)!.Name.Length);
        }
        else
        {
            var refused = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
            Assert.Equal(changed.Position, database.Position);
            Assert.Equal(11, pieces.Get(1L)!.Name.Length);
        }

        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    [Fact]
    public async Task A_child_applier_reclaims_a_viewers_memory_while_a_second_view_waits_for_its_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: ChildValue));
        var options = ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound,
            memoryLimit: InstanceBudget.OneSegmentWithReceive);
        var pressure = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var child = await ReplicationNodes.OpenChildAsync(options, new EngineTestInputs
        {
            ObserveChildPressure = observed => pressure.TrySetResult(observed),
        });
        var recordSet = child.Set("Piece");
        using var pause = new ViewerPause();
        var reading = ReadPiece(1L, recordSet, pause);
        await pause.EnteredAsync(reading);

        using var applying = child.SuspendApplying();
        using var cancel = new CancellationTokenSource();

        try
        {
            var beforeDelete = child.Position;
            var deleted = await pieces.DeleteAsync(1L);
            var inserted = await pieces.InsertAsync(PieceSchema.Of(2, name: 11, data: ChildValue));

            // This read starts before the delete can retire anything. An early epoch
            // would keep that memory, leaving the applier unable to reach the second view.
            var session = new LatticeSession(inserted.Position) { SynchronizationTimeout = Bound + Bound };
            var following = recordSet.ViewAsync(2L, session, static view => view["Name"].GetString().Length, cancel.Token).AsTask();
            Assert.False(following.IsCompleted);
            Assert.Equal(beforeDelete, child.Position);

            var waiting = RefusedWriteWait.Begins(child);
            applying.Dispose();
            await child.WaitForPositionAsync(deleted.Position, Bound);
            await waiting.WaitAsync(Bound);
            var first = await pressure.Task.WaitAsync(Bound);
            Assert.Equal(LatticeLimitEnum.Memory, first.Limit);
            Assert.False(first.DuringReceive);

            Assert.Null(recordSet.Get(1L));
            Assert.Equal(deleted.Position, child.Position);
            Assert.False(following.IsCompleted);
            Assert.True(child.Counters.Arena.RetiredBytes > 0);
            pause.Release();

            Assert.Equal((10, ChildValue), await reading.WaitAsync(Bound));
            var answer = await following.WaitAsync(Bound);
            Assert.True(answer.Found);
            Assert.Equal(11, answer.Result);
            Assert.Equal(inserted.Position, answer.Position);
            Assert.Equal(answer.Position, session.MinPosition);
            await child.WaitForPositionAsync(inserted.Position, Bound);
            Assert.Equal(11, recordSet.Get(2L)!.Get<string>("Name")!.Length);
            Assert.Equal(0, child.Counters.Arena.RetiredBytes);
        }
        finally
        {
            pause.Release();
            await cancel.CancelAsync();
        }
    }

    private static Task<(int Name, int Bytes)> ReadPiece(long key, ILatticeSet pieces, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(pieces.TryView(key, pause, static (view, held) =>
            {
                held.Hold();
                return (Name: view["Name"].GetString().Length, Bytes: view["Data"].ByteLength);
            }, out var shape));

            return shape;
        });
}
