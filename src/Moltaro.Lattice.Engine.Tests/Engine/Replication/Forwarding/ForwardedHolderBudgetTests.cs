using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The master holds the received pieces of a forwarded request and its import workspace as
// connection buffers, charged by their arrays, while the request is being received; a reply its
// sender cannot send yet stays held, and the child completes the write once the reply goes.
public sealed class ForwardedHolderBudgetTests
{
    [Fact]
    public async Task Master_charges_received_pieces_and_import_workspace_and_holds_a_reply_for_a_blocked_sender()
    {
        var allocator = new CountingNativeAllocator();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EngineDatabase? accepting = null;
        await using var master = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = PieceSchema.Schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = MasterAndChild.Loopback,
            MemoryLimit = 64L * Segment,
        }, new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforeMasterReplyDrain = async closing =>
            {
                beforeDrain.TrySetResult();
                await releaseDrain.Task.WaitAsync(Bound, closing);
            },
            ObserveMasterRequestFrame = _ =>
            {
                if (received.Task.IsCompleted)
                {
                    return;
                }

                // Outside the writes lock, before the completed request goes to the write path.
                var held = ExpectInFlight(accepting!, allocator, UnitEstimate(1));
                ExpectOwners(held, "MessageAssembler", "BlockImport");
                Assert.Contains(held.Arrays, array =>
                    array.Owner == "MessageAssembler" && array.ElementType == typeof(byte) && array.Length >= 32 * 1024);
                received.TrySetResult();
            },
        });
        accepting = master;
        await master.OpenAsync().AsTask().WaitAsync(Bound);
        var source = master.Set<PieceSchema.Piece>();
        var ready = await source.InsertAsync(PieceSchema.Of(1, 8));
        await using var link = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(link.Address), new EngineTestInputs());
        var local = child.Set<PieceSchema.Piece>();
        Assert.Equal(ready.Position, child.Position);

        var writing = local.InsertAsync(PieceSchema.Of(3, 8, data: 170_000)).AsTask();
        try
        {
            await received.Task.WaitAsync(Bound);
            await beforeDrain.Task.WaitAsync(Bound);
            Assert.Equal(ready.Position.Sequence + 1, master.Position.Sequence);
            Assert.Equal(170_000, source.Get(3L)?.Data?.Length);
            ExpectInFlight(master, allocator, UnitEstimate(1));
            Assert.DoesNotContain(link.ToChild, frame => frame.Kind == FrameKindEnum.WriteReply);
            Assert.False(writing.IsCompleted);
            Assert.Equal(ready.Position, child.Position);

            releaseDrain.TrySetResult();
            var applied = await writing.WaitAsync(Bound);
            Assert.Equal(master.Position, applied.Position);
            Assert.Equal(170_000, local.Get(3L)?.Data?.Length);
            await Eventually.HoldsAsync(() => link.ToChild.Any(frame => frame.Kind == FrameKindEnum.WriteReply),
                "The held reply did not reach the child.");
        }
        finally
        {
            releaseDrain.TrySetResult();
        }
    }
}
