using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

public sealed class MalformedUpdateRequestTests
{
    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData("unbounded count")]
    [InlineData("unknown target")]
    [InlineData("duplicate scalar")]
    public async Task A_malformed_update_request_ends_the_connection_without_executing(string damage)
    {
        const string Key = "ada";
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item(Key));
        byte[] request;
        await using (var relay = FrameRelay.Start(master.ListenUri!))
        {
            await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
            var update = child.Set("Item").CreateUpdate(Key);
            update.Assign("Login", "sparse");
            await update.CommitAsync();
            var frame = Assert.Single(relay.FromChild, frame => frame.Kind == ForwardPayload.WriteRequest);
            Assert.True(frame.EndsABody);
            request = ForwardPayload.BodyOf([frame]);
        }

        Assert.Equal(5, request[0]);
        var lengthAt = 1 + sizeof(uint) + 2 + ForwardPayload.KeyOf(Key).Length;
        var countAt = lengthAt + sizeof(ulong);
        var commandAt = countAt + sizeof(uint);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(countAt)));

        switch (damage)
        {
            case "unbounded count":
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(countAt), uint.MaxValue);
                break;

            case "unknown target":
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(commandAt + 1), uint.MaxValue);
                break;

            case "duplicate scalar":
                var second = request[commandAt..];
                request = [.. request, .. second];
                BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(lengthAt), (ulong)(request.Length - countAt));
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(countAt), 2);
                break;
        }

        Assert.True(items.TryGet(Key, out _, out var before));
        var position = master.Position;
        using var peer = await LoadedPeer.ConnectAsync(master);
        await peer.SendRequestAsync(1, request);

        Assert.Null(await peer.ReceiveReplyOrTheEndAsync(1));
        Assert.Equal(position, master.Position);
        Assert.True(items.TryGet(Key, out var unchanged, out var after));
        Assert.Equal(before, after);
        Assert.Equal("sparse", unchanged.Login);
    }
}
