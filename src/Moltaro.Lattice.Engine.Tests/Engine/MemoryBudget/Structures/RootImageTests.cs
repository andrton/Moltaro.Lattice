using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// The root image of ordinary writes is one workspace of the arena, charged once at its size: a
// write whose image fits takes the same one, and a wider image replaces it, released as the new one
// is charged and no longer held by the arena.
public sealed class RootImageTests
{
    [Fact(Skip = Pending)]
    public async Task The_arena_keeps_one_root_image_that_fits_and_replaces_it_when_a_wider_one_is_needed()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(8L * Segment, allocator);
        await node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        var rest = ExpectAtRest(node, allocator);

        var (first, wider, replaced) = await MaintenanceStep.RunAfterAdmissionAsync(node, parts => Replace(parts.Arena));
        Assert.InRange(first, 64, int.MaxValue);
        Assert.InRange(wider, first + 1, int.MaxValue);
        var after = ExpectAtRest(node, allocator);
        Assert.Equal(ArrayBytes(typeof(byte), wider) - ArrayBytes(typeof(byte), first), after.Charged - rest.Charged);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(replaced.IsAlive);
    }

    // Under the writes lock: an image that fits is the same one, a wider one replaces it and is kept
    // for every narrower request.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int First, int Wider, WeakReference Replaced) Replace(Moltaro.Lattice.Storage.Arena arena)
    {
        var first = arena.RootImage(64);
        Assert.Same(first, arena.RootImage(first.Length));
        var wider = arena.RootImage(first.Length + 1);
        Assert.NotSame(first, wider);
        Assert.Same(wider, arena.RootImage(1));
        return (first.Length, wider.Length, new WeakReference(first));
    }
}
