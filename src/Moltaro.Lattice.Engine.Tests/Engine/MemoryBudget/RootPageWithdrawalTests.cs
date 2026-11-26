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

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class RootPageWithdrawalTests
{
    [Fact]
    public async Task Cancelling_the_first_prepared_insert_returns_its_unused_page_and_directory()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using (var node = await OpenAsync(16L * Segment, allocator, persistence: storage))
        {
            var pieces = node.Set<PieceSchema.Piece>();
            var start = node.Position;
            storage.Hold();
            try
            {
                var predecessor = pieces.DeleteAsync(99L).AsTask();
                await storage.NextAsync();
                var original = await DirectoryAsync(node);
                Assert.Empty(original);
                var before = ExpectCapacity(node, allocator);
                Assert.Equal(0, before.NativeBytes);

                using var stop = new CancellationTokenSource();
                var inserting = pieces.InsertAsync(PieceSchema.Of(1, 8), stop.Token).AsTask();
                var added = await DirectoryAsync(node);
                var page = Assert.Single(added);
                var owned = new object[] { added, page };
                var physical = owned.Sum(ManagedObjectSize);
                Assert.Equal(196_664L, physical);
                _ = ExpectCapacity(node, allocator, out _, owned);
                Assert.Equal(Segment, allocator.OutstandingBytes);
                Assert.False(inserting.IsCompleted);
                Assert.Equal(start, node.Position);

                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inserting.WaitAsync(Bound));
                Assert.False(predecessor.IsCompleted);
                Assert.Equal(start, node.Position);
                Assert.Null(pieces.Get(1L));
                Assert.Same(original, await DirectoryAsync(node));
                var after = ExpectCapacity(node, allocator);
                Assert.Equal(before.ChargedBytes, after.ChargedBytes);
                Assert.Equal(0, after.NativeBytes);
                Assert.Equal(1, allocator.Returned);

                storage.ReleaseAll();
                await predecessor.WaitAsync(Bound);
                var published = await pieces.InsertAsync(PieceSchema.Of(1, 8));
                Assert.Equal(published.Position, node.Position);
                Assert.Equal(1, pieces.Get(1L)?.Count);
            }
            finally
            {
                storage.ReleaseAll();
            }
        }

        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Cancelling_a_later_directory_growth_restores_the_directory_of_the_earlier_queued_batch()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(16L * Segment, allocator, persistence: storage);
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Tables.Stored(1).Presize(RootTable.EntriesPerPage + 1);
            return true;
        });
        node.DrainRetired();
        var pieces = node.Set<PieceSchema.Piece>();

        storage.Hold();
        try
        {
            var predecessor = pieces.DeleteAsync(99_999L).AsTask();
            await storage.NextAsync();
            var batch = node.CreateWriteBatch();
            for (var key = 1; key <= RootTable.EntriesPerPage; key++)
            {
                batch.Insert(PieceSchema.Of(key, 8));
            }

            var earlier = batch.CommitAsync().AsTask();
            var shared = await DirectoryAsync(node);
            var page = Assert.Single(shared);
            var before = ExpectCapacity(node, allocator, out _, shared, page);
            using var stop = new CancellationTokenSource();
            // Both tables use the same root address space. Use the other primary index to
            // keep the earlier batch's key reservations from growing the overflow table.
            var holders = node.Set("Holder");
            const long keyToWithdraw = RootTable.EntriesPerPage + 1L;
            var withdrawn = holders.InsertAsync(new LatticeRecord(holders.EntityType)
            {
                ["Id"] = keyToWithdraw,
                ["Lines"] = new Dictionary<string, string>(),
            }, stop.Token).AsTask();
            var expanded = await DirectoryAsync(node);
            Assert.Equal(2, expanded.Length);
            Assert.Same(page, expanded[0]);
            var overlap = ExpectCapacity(node, allocator, out _, shared, page, expanded, expanded[1]);
            var added = ManagedObjectSize(expanded) + ManagedObjectSize(expanded[1]);
            Assert.Equal(196_672L, added);
            Assert.Equal(before.ChargedBytes + added, overlap.ChargedBytes);

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn.WaitAsync(Bound));
            Assert.False(earlier.IsCompleted);
            Assert.False(predecessor.IsCompleted);
            Assert.Same(shared, await DirectoryAsync(node));
            var after = ExpectCapacity(node, allocator, out _, shared, page);
            Assert.Equal(before.ChargedBytes, after.ChargedBytes);
            Assert.Equal(before.NativeBytes, after.NativeBytes);
            _ = node.InspectBudget((_, backing) => Assert.NotSame(expanded, backing));

            storage.ReleaseAll();
            await predecessor.WaitAsync(Bound);
            var committed = await earlier.WaitAsync(Bound);
            Assert.Equal(committed.Position, node.Position);
            Assert.Equal(RootTable.EntriesPerPage, pieces.Enumerate().Count());
            Assert.Null(holders.Get(keyToWithdraw));
            Assert.Same(shared, await DirectoryAsync(node));
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

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
            var shared = await DirectoryAsync(node);
            var page = Assert.Single(shared);
            var keeping = pieces.InsertAsync(PieceSchema.Of(1, 16)).AsTask();
            _ = ExpectCapacity(node, allocator, out _, shared, page);

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
            Assert.Same(shared, await DirectoryAsync(node));
            _ = ExpectCapacity(node, allocator, out _, shared, page);
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
            Assert.Same(shared, await DirectoryAsync(node));
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
        var original = await DirectoryAsync(master);
        var livePage = Assert.Single(original);
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
            var expanding = await DirectoryAsync(master);
            Assert.Equal(2, expanding.Length);
            Assert.Same(livePage, expanding[0]);
            _ = ExpectCapacity(master, allocator, out _, original, expanding, expanding[1]);

            var opening = child.OpenAsync().AsTask();
            await held.HeldAsync();
            await Eventually.HoldsAsync(() => child.State == LatticeDatabaseStateEnum.Loading && master.Counters.ActiveCaptures == 1,
                "The master must still be sending the capture while the queued write is withdrawn.");

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawing.WaitAsync(Bound));
            Assert.False(opening.IsCompleted);
            Assert.Equal(1, master.Counters.ActiveCaptures);
            Assert.Equal(start, master.Position);
            Assert.Same(original, await DirectoryAsync(master));
            Assert.Equal(initial, Snapshot(master));
            Assert.Null(pieces.Get(cancelled));

            view.Release();
            Assert.Equal((1L, 1), await oldView.WaitAsync(Bound));
            storage.ReleaseAll();
            var earlier = await predecessor.WaitAsync(Bound);
            var changed = await pieces.ReplaceAsync(PieceSchema.Of(2, 21));
            Assert.NotEqual(earlier.Position, changed.Position);
            Assert.Equal(1, master.Counters.ActiveCaptures);
            Assert.Same(original, await DirectoryAsync(master));

            held.Release();
            await opening.WaitAsync(Bound);
            Assert.Equal(changed.Position, child.Position);
            var local = child.Set<PieceSchema.Piece>();
            Assert.Equal(large.Data, local.Get(1L)?.Data);
            Assert.Equal(21, local.Get(2L)?.Name.Length);
            Assert.Null(local.Get(cancelled));
            Assert.Equal(initial, Snapshot(master));
            Assert.Same(original, await DirectoryAsync(master));

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

    private static Task<RootEntry[][]> DirectoryAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => Backing<RootEntry[][]>(parts.Roots, "_pages"));
}
