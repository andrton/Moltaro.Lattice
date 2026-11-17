using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Loading;

// The capture of a load walks the slots of the root table of its master, not its primary index (brief of
// Epic 1, sprint S10, the planner's block of 17 November 2026, point 3), so it meets slots that no read
// meets: a slot that an insert took and gave back when the arena refused the entity holds no root and
// carries no stamp of a published write. A child loads the database of a master that has such a slot,
// and nothing of the refused insert is in it. Added by the implementer of S10.
public sealed class GivenBackSlotTests
{
    [Fact]
    public async Task Child_loads_from_a_master_whose_root_table_holds_the_slot_of_an_insert_the_arena_refused()
    {
        var inputs = new EngineTestInputs { Arena = new ArenaOptions { MemoryLimit = new ArenaOptions().SegmentSize } };
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, inputs);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);

        var refused = await Assert.ThrowsAsync<LatticeException>(async () => await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(3, name: 8, data: 8 * 1024 * 1024)));
        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        Pieces.ExpectSame(master, child);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(3L));
        Assert.Equal(master.Position, child.Position);
    }
}
