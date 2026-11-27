using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// A request a child forwards reserves its estimate on the master when its handling starts, before its
// roots are checked, and the check of a root takes memory of its own: the first root of Piece that the
// import of a connection checks grows the starts of its three descriptors to a long[3]. A request the
// master cannot hold at either point is refused with WriteRejected and Memory, the child keeps its
// master, and the same write is accepted once memory is freed. The test holds the memory of the master
// once the request has arrived whole, before its handling starts, leaving the room given.
public sealed class ForwardedRequestRefusalTests
{
    private const long Limit = 16L * Segment;

    public static IEnumerable<TheoryDataRow<string, bool>> Rooms =>
    [
        new("nothing", false),
        new("less than the estimate", false),
        new("the estimate but not the check of the root", false),
        new("nothing", true),
        new("less than the estimate", true),
        new("the estimate but not the check of the root", true),
    ];

    [Theory]
    [MemberData(nameof(Rooms))]
    public async Task A_forwarded_write_the_master_cannot_hold_is_refused_with_Memory_and_the_child_keeps_its_master(string room, bool batch)
    {
        // A held array leaves between the room and 7 bytes more (LeaveAbout).
        var startsOfPiece = ArrayBytes(typeof(long), 3);
        var left = room switch
        {
            "nothing" => 0,
            "less than the estimate" => BatchInputCharge.PerMemberBytes - 8,
            _ => BatchInputCharge.PerMemberBytes + startsOfPiece - 8,
        };
        var allocator = new CountingNativeAllocator();
        IDisposable? held = null;
        EngineDatabase? accepting = null;
        await using var master = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = PieceSchema.Schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = MasterAndChild.Loopback,
            MemoryLimit = Limit,
        }, new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            ObserveMasterRequestFrame = receiver => held ??= LeaveAbout(accepting!, left, out _),
        });
        accepting = master;
        await master.OpenAsync().AsTask().WaitAsync(Bound);
        var ready = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { DisableBackgroundMaintenance = true });
        await child.WaitForPositionAsync(ready.Position, Bound);

        try
        {
            var refused = await Assert.ThrowsAsync<LatticeException>(() => WriteAsync(child, batch).WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
            Assert.Null(refused.OperationIndex);
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Null(master.Set<PieceSchema.Piece>().Get(2L));
        ExpectAtRest(master, allocator);

        var written = await WriteAsync(child, batch).WaitAsync(Bound);
        await child.WaitForPositionAsync(written.Position, Bound);
        Assert.Equal(10, child.Set<PieceSchema.Piece>().Get(2L)?.Note?.Length);
    }

    private static async Task<LatticeWriteResult> WriteAsync(EngineDatabase child, bool batch)
    {
        var piece = PieceSchema.Of(2, 8, note: 10);
        if (!batch)
        {
            return await child.Set<PieceSchema.Piece>().InsertAsync(piece);
        }

        var writes = child.CreateWriteBatch();
        writes.Insert(piece);
        return await writes.CommitAsync();
    }
}
