using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.TestSupport.Concurrency;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Capacity;

public sealed class RootPageWithdrawalTests
{
    [Fact]
    public async Task Cancelling_one_insert_keeps_the_shared_page_for_the_next_prepared_root_and_child_capture()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(16L * Segment, allocator, listen: true, persistence: storage);
        var pieces = node.Set<PieceSchema.Piece>();
        var start = node.Position;
        storage.Hold();
        try
        {
            var predecessor = pieces.DeleteAsync(99L).AsTask();
            await storage.NextAsync();
            using var stop = new CancellationTokenSource();
            var withdrawing = pieces.InsertAsync(PieceSchema.Of(2, 8), stop.Token).AsTask();
            var keeping = pieces.InsertAsync(PieceSchema.Of(1, 16)).AsTask();

            await using var child = await ReplicationNodes.OpenChildAsync(node.ListenUri!);
            Assert.Equal(start, child.Position);
            var local = child.Set<PieceSchema.Piece>();
            Assert.Null(local.Get(1L));
            Assert.Null(local.Get(2L));

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawing.WaitAsync(Bound));
            Assert.False(predecessor.IsCompleted);
            Assert.False(keeping.IsCompleted);
            Assert.Equal(start, node.Position);
            Assert.Equal(start, child.Position);
            Assert.Equal(Segment, allocator.OutstandingBytes);

            storage.ReleaseAll();
            var preceding = await predecessor.WaitAsync(Bound);
            var published = await keeping.WaitAsync(Bound);
            Assert.NotEqual(preceding.Position, published.Position);
            await child.WaitForPositionAsync(published.Position, Bound);
            Assert.Equal(published.Position, child.Position);
            Assert.Equal(16, local.Get(1L)?.Name.Length);
            Assert.Null(local.Get(2L));
            Assert.True(pieces.TryGet(1L, out _, out var masterRevision));
            Assert.True(local.TryGet(1L, out _, out var childRevision));
            Assert.Equal(masterRevision, childRevision);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task A_capture_in_progress_never_needs_the_exclusively_cancelled_page()
    {
        const long cancelled = RootTable.EntriesPerPage + 1L;
        const long later = cancelled + 1;
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var master = await OpenAsync(16L * Segment, allocator, listen: true, persistence: storage);
        var pieces = master.Set<PieceSchema.Piece>();
        var large = PieceSchema.Of(1, 8, data: 6 * 1024 * 1024);
        large.Data![0] = 77;
        large.Data[^1] = 99;
        await pieces.InsertAsync(large);
        for (var key = 2; key <= RootTable.EntriesPerPage; key++)
        {
            await pieces.InsertAsync(PieceSchema.Of(key, 8));
        }

        master.DrainRetired();
        var initial = Snapshot(master);
        var start = master.Position;

        using var view = new ViewerPause();
        await using var held = HeldLink.Start(master.ListenUri!);
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(held.Address, ReplicationNodes.BeyondTheBound),
            new EngineTestInputs());
        storage.Hold();
        try
        {
            var oldView = HoldPieceView(master, view);
            await view.EnteredAsync(oldView);
            var predecessor = pieces.DeleteAsync(99_999L).AsTask();
            await storage.NextAsync();
            using var stop = new CancellationTokenSource();
            var withdrawing = pieces.InsertAsync(PieceSchema.Of(cancelled, 20), stop.Token).AsTask();

            var opening = child.OpenAsync().AsTask();
            await held.HeldAsync();
            await Eventually.HoldsAsync(() => child.State == LatticeDatabaseStateEnum.Loading && master.Counters.ActiveCaptures == 1,
                "The master must still be sending the capture while the queued write is withdrawn.");

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawing.WaitAsync(Bound));
            Assert.False(opening.IsCompleted);
            Assert.Equal(1, master.Counters.ActiveCaptures);
            Assert.Equal(start, master.Position);
            Assert.Equal(initial, Snapshot(master));
            Assert.Null(pieces.Get(cancelled));

            view.Release();
            Assert.Equal((1L, 1), await oldView.WaitAsync(Bound));
            storage.ReleaseAll();
            var earlier = await predecessor.WaitAsync(Bound);
            var changed = await pieces.ReplaceAsync(PieceSchema.Of(2, 21));
            Assert.NotEqual(earlier.Position, changed.Position);
            Assert.Equal(1, master.Counters.ActiveCaptures);

            held.Release();
            await opening.WaitAsync(Bound);
            Assert.Equal(changed.Position, child.Position);
            var local = child.Set<PieceSchema.Piece>();
            Assert.Equal(large.Data, local.Get(1L)?.Data);
            Assert.Equal(21, local.Get(2L)?.Name.Length);
            Assert.Null(local.Get(cancelled));
            Assert.Equal(initial, Snapshot(master));

            var written = await pieces.InsertAsync(PieceSchema.Of(later, 18));
            await child.WaitForPositionAsync(written.Position, Bound);
            Assert.Equal(written.Position, child.Position);
            Assert.Equal(18, local.Get(later)?.Name.Length);
            _ = ExpectCapacity(master, allocator);
        }
        finally
        {
            view.Release();
            storage.ReleaseAll();
            held.Release();
        }
    }

    private static (RootHandle Handle, ArenaAddress Address, LatticeRevision Revision) Snapshot(EngineDatabase node)
    {
        RootHandle handle = default;
        ArenaAddress address = default;
        node.ForEachLiveRoot((arena, layout, rootHandle, rootAddress) =>
        {
            var reader = new RootReader(arena, layout, rootAddress);
            if (reader.GetInteger(layout.EntityType.Key) == 1)
            {
                handle = rootHandle;
                address = rootAddress;
            }
        });
        Assert.NotEqual(default, handle);
        Assert.NotEqual(default, address);
        Assert.True(node.Set<PieceSchema.Piece>().TryGet(1L, out _, out var revision));
        return (handle, address, revision);
    }
}
