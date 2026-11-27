using System.Reflection;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class RootSlotBudgetTests
{
    internal const int PageSlots = 4_096;
    internal const long Room = 64L * Segment;

    [Fact]
    public void The_production_geometry_has_524287_pages_of_4096_slots()
    {
        var pages = Assert.IsType<int>(typeof(RootTable).GetField("MostPages", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue());
        Assert.Equal(524_287, pages);
        Assert.Equal(PageSlots, RootTable.EntriesPerPage);
        Assert.Equal(2_147_479_552, pages * RootTable.EntriesPerPage);
    }

    [Fact]
    public async Task On_a_full_page_an_insert_waits_for_a_deleted_slot_and_then_reuses_its_next_generation()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Room, allocator, rootPageLimit: 1);
        await FillAsync(node, PageSlots - 1);
        Assert.Equal(PageSlots - 1, node.Counters.RootEntries);

        var pieces = node.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(PageSlots, 8));
        Assert.Equal(PageSlots, node.Counters.RootEntries);
        // The full-page baseline excludes unrelated, reader-free primary generations.
        node.DrainRetired();
        var full = ExpectCapacity(node, allocator);
        var current = node.Position;
        await RefusedAsync(pieces.InsertAsync(PieceSchema.Of(PageSlots + 1, 8)).AsTask(), LatticeLimitEnum.RootSlots);
        Assert.Equal(current, node.Position);
        Assert.Equal(full.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);

        var heldHandle = HandleOf(node, 1);
        using var pause = new ViewerPause();
        var held = HoldPieceView(node, pause);
        await pause.EnteredAsync(held);
        await pieces.DeleteAsync(1L);
        Assert.Equal(PageSlots, node.Counters.RootEntries);
        var deletedAt = node.Position;
        var needsSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.ArmWaitSwitch(() => needsSlot.TrySetResult());
        var waiting = pieces.InsertAsync(PieceSchema.Of(1, 8)).AsTask();
        await needsSlot.Task.WaitAsync(Bound);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(deletedAt, node.Position);
        Assert.Null(pieces.Get(1L));

        pause.Release();
        Assert.Equal(1, (await held.WaitAsync(Bound)).Count);
        await waiting.WaitAsync(Bound);
        node.DrainRetired();
        var actual = HandleOf(node, 1);
        Assert.Equal(heldHandle.Slot, actual.Slot);
        Assert.Equal(heldHandle.Generation + 1, actual.Generation);
        Assert.Equal(PageSlots, node.Counters.RootEntries);
        Assert.Null(pieces.Get(PageSlots + 1L));
        Assert.Equal(full.NativeBytes, ExpectCapacity(node, allocator).NativeBytes);
        Assert.Equal(full.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
    }

    [Fact]
    public async Task A_final_generation_cannot_supply_a_reusable_address_from_a_full_page()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Room, allocator, rootPageLimit: 1);
        await FillAsync(node, PageSlots - 2);
        var (ordinary, final) = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var table = parts.Roots;
            var reusable = table.Add(1);
            var last = table.Add(1);
            table.Entry(last).Generation = uint.MaxValue;
            var neverReusable = new RootHandle(uint.MaxValue, last.Slot);
            table.Release(neverReusable);
            table.Release(reusable);
            return (reusable, neverReusable);
        });

        var pieces = node.Set<PieceSchema.Piece>();
        var admitted = await pieces.InsertAsync(PieceSchema.Of(PageSlots - 1, 8));
        var reused = HandleOf(node, PageSlots - 1);
        Assert.Equal(ordinary.Slot, reused.Slot);
        Assert.Equal(ordinary.Generation + 1, reused.Generation);
        await RefusedAsync(pieces.InsertAsync(PieceSchema.Of(PageSlots, 8)).AsTask(), LatticeLimitEnum.RootSlots);
        Assert.Equal(admitted.Position, node.Position);
        Assert.Null(pieces.Get((long)PageSlots));
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var table = parts.Roots;
            Assert.False(table.IsCurrent(final));
            Assert.False(table.IsCurrent(ordinary));
            Assert.True(table.IsCurrent(reused));
            Assert.Equal(RootStateEnum.Retired, table.EntryOfSlot(final.Slot).State);
            Assert.Equal(1, table.Pages);
            Assert.Equal(PageSlots, table.SlotExtent);
            Assert.Equal(PageSlots - 1, table.Count);
            return true;
        });
    }

    [Theory]
    [InlineData(LatticeLimitEnum.Memory)]
    [InlineData(LatticeLimitEnum.RootSlots)]
    public async Task A_resource_refused_forwarded_batch_names_no_member_and_leaves_values_revisions_and_positions_unchanged(LatticeLimitEnum limit)
    {
        var allocator = new CountingNativeAllocator();
        var slots = limit == LatticeLimitEnum.RootSlots;
        await using var master = await OpenAsync(slots ? Room : 15L * 1024 * 1024, allocator,
            rootPageLimit: slots ? 1 : null, listen: true);
        if (slots)
        {
            await FillAsync(master, PageSlots);
        }
        else
        {
            await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8, data: 3 * 1024 * 1024));
        }
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var source = master.Set<PieceSchema.Piece>();
        var mirror = child.Set<PieceSchema.Piece>();
        var initial = master.Position;
        Assert.True(source.TryGet(1L, out var expected, out var original));
        Assert.Equal(initial, child.Position);
        var before = ExpectCapacity(master, allocator);
        var obtained = allocator.ObtainedBytes;

        var batch = child.CreateWriteBatch();
        batch.Replace(PieceSchema.Of(1, 17));
        var newKey = slots ? PageSlots + 1L : 2L;
        batch.Insert(PieceSchema.Of(newKey, 12, data: slots ? 0 : 6 * 1024 * 1024));
        await RefusedAsync(batch.CommitAsync().AsTask(), limit);
        if (!slots)
        {
            // The master admitted and prepared the forwarded batch far enough to
            // obtain a second segment, before the third grant could be refused.
            Assert.True(allocator.ObtainedBytes > obtained);
        }

        Assert.Equal(initial, master.Position);
        Assert.Equal(initial, child.Position);
        Assert.Null(source.Get(newKey));
        Assert.Null(mirror.Get(newKey));
        Assert.True(source.TryGet(1L, out var actual, out var final));
        Assert.True(mirror.TryGet(1L, out var replicated, out var childRevision));
        Assert.Equal(original, final);
        Assert.Equal(original, childRevision);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Name, replicated.Name);
        // A segment the refusal left empty is released or kept as the one empty reserve.
        Assert.InRange(ExpectCapacity(master, allocator).NativeBytes, before.NativeBytes, before.NativeBytes + Segment);
    }

    internal static async Task FillAsync(EngineDatabase node, int count)
    {
        var pieces = node.Set<PieceSchema.Piece>();
        for (var key = 1L; key <= count; key++)
        {
            await pieces.InsertAsync(PieceSchema.Of(key, 8));
        }
    }

    private static RootHandle HandleOf(EngineDatabase node, long key)
    {
        RootHandle found = default;
        node.ForEachLiveRoot((arena, layout, handle, address) =>
        {
            var root = new RootReader(arena, layout, address);
            if (root.GetInteger(layout.EntityType.Key) == key)
            {
                found = handle;
            }
        });
        Assert.NotEqual(default, found);
        return found;
    }
}
