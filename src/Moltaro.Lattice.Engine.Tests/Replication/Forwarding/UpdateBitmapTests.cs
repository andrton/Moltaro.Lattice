using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The correlated reply has one outcome for every command, including no-ops, in the order
// submitted. It is checked before it permits success (decisions/mutation-commands.md).
public sealed class UpdateBitmapTests
{
    [Fact(Skip = UpdateSchema.Pending)]
    public async Task A_large_bitmap_uses_multiple_reply_frames_and_keeps_every_bit_in_request_order()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        await master.Set<UpdateItem>().InsertAsync(UpdateSchema.Item("ada"));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var update = child.Set("Item").CreateUpdate("ada");
        var count = Protocol.MaxDataPayload * 8 + 1;
        var commands = new LatticeCommandResult[count];
        commands[0] = update.SetAdd("Tags", "first");
        for (var index = 1; index < count - 1; index++)
        {
            commands[index] = update.SetRemove("Tags", "");
        }

        commands[^1] = update.SetAdd("Tags", "last");
        var result = await update.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.True(result.Changed);
        Assert.True(commands[0].Changed);
        Assert.True(commands[^1].Changed);
        for (var index = 1; index < count - 1; index++)
        {
            Assert.False(commands[index].Changed);
        }

        var frames = relay.FromMaster.ToList();
        var reply = frames.Where(frame => frame.Kind == ForwardPayload.WriteReply).ToArray();
        Assert.True(reply.Length > 1);
        Assert.All(reply, frame => Assert.Equal(reply[0].CorrelationId, frame.CorrelationId));
        Assert.True(frames.FindLastIndex(frame => frame.Kind == ForwardPayload.WriteReply) <
            frames.FindIndex(frame => frame.Kind == FrameKindEnum.Transaction));

        var offset = ForwardPayload.Result(result.Position, changed: true).Length;
        var body = ForwardPayload.BodyOf(reply);
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)));
        Assert.Equal(offset + sizeof(uint) + ((count + 7) / 8), body.Length);
        Assert.Equal(1, body[offset + sizeof(uint)]);
        Assert.Equal(1, body[^1]);
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData("count")]
    [InlineData("same bitmap length")]
    [InlineData("padding")]
    [InlineData("length")]
    [InlineData("extra framed byte")]
    [InlineData("aggregate")]
    public async Task A_malformed_correlated_bitmap_ends_the_connection_and_exposes_no_command_outcome(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        await master.Set<UpdateItem>().InsertAsync(UpdateSchema.Item("ada"));
        var previous = master.Position;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != ForwardPayload.WriteReply)
            {
                return [frame];
            }

            var offset = ForwardPayload.Result(previous, changed: true).Length;
            var body = ForwardPayload.BodyOf([frame]);
            if (damage == "count")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), uint.MaxValue);
            }
            else if (damage == "same bitmap length")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), 2);
            }
            else if (damage == "padding")
            {
                body[^1] |= 0x80;
            }
            else if (damage == "aggregate")
            {
                body[offset - 1] = 0;
            }
            else if (damage == "extra framed byte")
            {
                body = [.. body, 0];
            }

            return ForwardPayload.Frames(ForwardPayload.WriteReply, frame.CorrelationId, frame.StreamSequence, body,
                announcedLength: damage == "length" ? (ulong)body.Length + 1 : null);
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var update = child.Set("Item").CreateUpdate("ada");
        var changed = update.SetAdd("Tags", "friend");
        var error = await Assert.ThrowsAsync<LatticeException>(() => update.CommitAsync().AsTask().WaitAsync(Bound));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, error.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(previous, child.Position);
        Assert.Throws<InvalidOperationException>(() => changed.Changed);
    }
}
