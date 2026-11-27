using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Boundaries;

// An addition to a batch reserves its estimate once its input is encoded: the encoded length of the
// root, or of the commands of an update, and PerMemberBytes; the first addition also PerBuilderBytes.
// With room for an estimate of E bytes, an addition of E + 1 is refused with Memory and changes
// nothing, one of E is admitted, and one of E - 1 leaves one byte; on a child the refusal sends no
// request. The encoded lengths come from the encoders the batch uses, which this sprint does not
// change, and grow by one byte for each further ASCII character.
public sealed class BatchAdditionBoundaryTests
{
    private const long Limit = 16L * Segment;

    [Theory(Skip = Pending)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_addition_is_admitted_exactly_when_its_estimate_fits(bool onChild, bool update)
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, masterAllocator, listen: true);
        var pieces = master.Set<PieceSchema.Piece>();
        LatticeWriteResult seeded = default;
        for (var key = 1L; key <= 3; key++)
        {
            seeded = await pieces.InsertAsync(PieceSchema.Of(key, 8));
        }

        await using var relay = FrameRelay.Start(master.ListenUri!);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = childAllocator, DisableBackgroundMaintenance = true });
        await child.WaitForPositionAsync(seeded.Position, Bound);
        var node = onChild ? child : master;
        var set = node.Set<PieceSchema.Piece>();
        var batch = node.CreateWriteBatch();
        batch.Delete<PieceSchema.Piece>(1_000L);
        Assert.Equal(1, batch.Count);

        var above = Aligned(node, update, extra: 0);
        using (Leave(node, Estimate(node, set, update, above)))
        {
            Assert.Equal(Estimate(node, set, update, above) + 1, Estimate(node, set, update, above + 1));
            var charged = node.InspectHeld().Charged;
            var refusal = Assert.Throws<LatticeException>(() => Add(batch, set, update, key: 1, above + 1));
            Assert.Equal(LatticeErrorEnum.WriteRejected, refusal.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refusal.Limit);
            Assert.Null(refusal.OperationIndex);
            Assert.Equal(1, batch.Count);
            Assert.Equal(charged, node.InspectHeld().Charged);

            Add(batch, set, update, key: 2, above);
            Assert.Equal(2, batch.Count);
            Assert.Equal(Limit, node.InspectHeld().Charged);
        }

        var below = Aligned(node, update, extra: 1);
        using (Leave(node, Estimate(node, set, update, below) + 1))
        {
            Add(batch, set, update, key: 3, below);
            Assert.Equal(3, batch.Count);
            Assert.Equal(Limit - 1, node.InspectHeld().Charged);
        }

        Assert.Equal(seeded.Position, master.Position);
        Assert.Equal(seeded.Position, child.Position);
        Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
        GC.KeepAlive(batch);
    }

    // The addition of a member of the given length of Name: an update of an existing entity, or a
    // new entity whose key the update would not address.
    private static void Add(ILatticeWriteBatch batch, ILatticeSet<PieceSchema.Piece> set, bool update, long key, int name)
    {
        if (update)
        {
            batch.Update(Update(set, key, name));
        }
        else
        {
            batch.Insert(PieceSchema.Of(10 + key, name));
        }
    }

    private static LatticeUpdate Update(ILatticeSet<PieceSchema.Piece> set, long key, int name)
    {
        var update = set.CreateUpdate(key);
        update.Assign("Name", new string('u', name));
        return update;
    }

    private static long Estimate(EngineDatabase node, ILatticeSet<PieceSchema.Piece> set, bool update, int name) =>
        update ? UpdateEstimate(node, Update(set, 1L, name)) : MemberEstimate(node, PieceSchema.Of(13, name));

    // The length of Name, from 16, at which room for the estimate and the extra bytes can be left
    // exactly by a held array, whose bytes are a multiple of 8.
    private static int Aligned(EngineDatabase node, bool update, int extra)
    {
        var set = node.Set<PieceSchema.Piece>();
        var free = Limit - node.InspectHeld().Charged;
        var name = Enumerable.Range(16, 8).Single(length => (free - Estimate(node, set, update, length) - extra) % 8 == 0);
        Assert.True(free - Estimate(node, set, update, name) - extra >= ArrayHeaderBytes);
        return name;
    }

    // Holds an array that leaves exactly the room given, which Aligned made a multiple of 8 away.
    private static IDisposable Leave(EngineDatabase node, long room)
    {
        var held = LeaveAbout(node, room, out var left);
        Assert.Equal(room, left);
        return held;
    }
}
