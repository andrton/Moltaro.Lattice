using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// The root image of ordinary writes is one workspace of the arena, charged once at its size: a
// write whose image fits takes the same one, and a wider image replaces it, released as the new one
// is charged.
public sealed class RootImageTests
{
    [Fact(Skip = Pending)]
    public async Task The_arena_keeps_one_root_image_that_fits_and_replaces_it_when_a_wider_one_is_needed()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(8L * Segment, allocator);
        await node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        var rest = ExpectAtRest(node, allocator);

        var (first, same, wider, narrow) = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var first = parts.Arena.RootImage(64);
            var same = parts.Arena.RootImage(first.Length);
            var wider = parts.Arena.RootImage(first.Length + 1);
            var narrow = parts.Arena.RootImage(1);
            return (first, same, wider, narrow);
        });

        Assert.InRange(first.Length, 64, int.MaxValue);
        Assert.Same(first, same);
        Assert.NotSame(first, wider);
        Assert.InRange(wider.Length, first.Length + 1, int.MaxValue);
        Assert.Same(wider, narrow);
        var after = ExpectAtRest(node, allocator);
        Assert.Equal(ArrayBytes(typeof(byte), wider.Length) - ArrayBytes(typeof(byte), first.Length), after.Charged - rest.Charged);
    }
}
