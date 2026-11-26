using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Children;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

public sealed class ForwardedHolderBudgetTests
{
    [Fact(Skip = Pending)]
    public async Task Master_charges_received_pieces_import_workspace_and_a_reply_still_held_for_a_blocked_sender()
    {
        var allocator = new CountingNativeAllocator();
        var received = new TaskCompletionSource<(RequestReceiver Receiver, object[] Import)>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            ObserveMasterRequestFrame = receiver =>
            {
                var assembler = Backing<MessageAssembler>(receiver, "_requests");
                if (Backing<MessagePiece?>(assembler, "_first") is not { } piece || received.Task.IsCompleted)
                {
                    return;
                }

                Assert.True(MemoryMarshal.TryGetArray(piece.Memory, out var memory));
                var bytes = memory.Array!;
                Assert.True(bytes.Length >= 32 * 1024);
                var import = Backing<object>(receiver, "_import");
                object[] workspace = [Backing<Array>(import, "_payload"), Backing<Array>(import, "_record"), Backing<Array>(import, "_inline")];
                _ = ExpectCapacity(accepting!, allocator, out _, [.. workspace, bytes, piece]);
                received.TrySetResult((receiver, workspace));
            },
        });
        accepting = master;
        await master.OpenAsync().AsTask().WaitAsync(Bound);
        var source = master.Set<PieceSchema.Piece>();
        var ready = await source.InsertAsync(PieceSchema.Of(1, 8));
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(link.Address), new EngineTestInputs());
        var local = child.Set<PieceSchema.Piece>();
        Assert.Equal(ready.Position, child.Position);

        var large = await source.InsertAsync(PieceSchema.Of(2, 8, data: 32 * 1024 * 1024)).AsTask().WaitAsync(Bound);
        await link.HeldAsync();
        var writing = local.InsertAsync(PieceSchema.Of(3, 8, data: 170_000)).AsTask();
        try
        {
            var (receiver, workspace) = await received.Task.WaitAsync(Bound);
            var replies = Backing<RecordedReplies>(receiver, "_replies");
            var pending = Backing<ConcurrentQueue<RecordedReply>>(replies, "_replies");
            await Eventually.HoldsAsync(() => pending.Count == 1, "The master did not retain the actual reply while its sender was blocked.");
            var answer = Assert.Single(pending);
            Assert.True(answer.Result.Changed);
            Assert.Equal(large.Position.Sequence + 1, answer.Result.Position.Sequence);
            Assert.Equal(answer.Result.Position, master.Position);
            var queue = Backing<object>(pending, "_head");
            var slots = Backing<Array>(queue, "_slots");
            Assert.NotEmpty(slots);
            _ = ExpectCapacity(master, allocator, out _, [.. workspace, slots]);
            Assert.False(writing.IsCompleted);
            Assert.Equal(ready.Position, child.Position);

            link.Release();
            var applied = await writing.WaitAsync(Bound);
            Assert.Equal(answer.Result.Position, applied.Position);
            Assert.Equal(170_000, local.Get(3L)?.Data?.Length);
            Assert.Empty(pending);
        }
        finally
        {
            link.Release();
        }
    }
}
