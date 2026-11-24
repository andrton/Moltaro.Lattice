using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// A write a child forwards is checked as a root received in a load is (BlockImport.Check), its tree included:
// a request whose tree holds a value inline at the location of another, which leaves as many bytes named by
// nothing, is what a child of the engine cannot have written. The master executes nothing of it and ends the
// connection without a reply, as for any request that is not valid (MalformedRequestTests), and holds the
// tree it had. The request comes from a child made by hand (LoadedPeer).
//
// Item: Id, an Int64; Tags, a List of String. The master holds the item 7 with the Tags "seven". The request
// upserts it with the Tags "ab" and "cd", both inline in the one block of the records, at 32 + 2 × 16 = 64 and
// 66; the second is given the location of the first.
public sealed class ForwardedInlineSpansTests
{
    // The envelope of a root: its type id, its last mutation and its member.
    private const int Envelope = 16;

    private const int Version = 4;

    private static readonly LatticeSchema Schema = ItemSchema.Compile("{ 'Id': 2, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } }");

    [Fact]
    public async Task Forwarded_write_whose_tree_holds_a_value_inline_at_the_location_of_another_ends_the_connection_and_is_never_executed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema);
        var items = master.Set("Item");

        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 7L, ["Tags"] = new[] { "seven" } });

        using var peer = await LoadedPeer.ConnectAsync(master);
        var root = RootWithTags(peer.Root, "ab", "cd");

        // The records of Tags follow the root payload, the Id and the descriptor of Tags.
        var second = Envelope + Version + 8 + ValueBlocks.DescriptorSize + ValueBlocks.DescriptorSize;

        Assert.Equal(66ul, BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(second + 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(root.AsSpan(second + 8), 64);

        await peer.SendRequestAsync(1, ForwardPayload.Request(ForwardPayload.Upsert, 1, ForwardPayload.KeyOf(7L), root));

        Assert.Null(await peer.ReceiveReplyOrTheEndAsync(1));
        Assert.Equal(["seven"], items.Get(7L)!.Get<List<string>>("Tags")!);
    }

    // The envelope of the root the peer holds, then the root of the item 7 with the Tags given, in the load
    // format, as a child writes the root of a write it forwards.
    private static byte[] RootWithTags(byte[] loaded, params string[] tags)
    {
        using var item = new RootArena(Schema);
        var record = new LatticeRecord(item.EntityType) { ["Id"] = 7L, ["Tags"] = tags };

        return [.. loaded[..Envelope], .. RootValues.Export(item.Layout, item.EntityType, [.. item.EntityType.Fields.Select(field => record[field.Name])]).ToArray()];
    }
}
