using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// How a load is cut into frames (brief of Epic 1, sprint S10, the planner's block of 15 November 2026,
// "A root larger than a frame", and his decision of 17 November after the study, point 3), read on the
// way from a real master to a real child by a relay that changes nothing (FrameRelay):
// - a root larger than a frame arrives in several Blocks frames, none with a body over the largest of a
//   data frame, and bit 0 of the flags marks the last of them only; the child holds the root whole;
// - small roots share a Blocks frame: fewer frames arrive than roots;
// - a schema larger than a frame is cut by the same rule: several Schema frames, none over the largest
//   body, the last of them marked; the child has the schema of its master.
// What the first frame of a root announces, and how a frame of small roots begins with their count, is
// the encoding of the body, which the tests of the received roots state through what a child refuses
// (ReceivedRootTests).
public sealed class LoadFramingTests
{
    [Fact]
    public async Task Root_larger_than_a_frame_arrives_in_several_frames_and_only_the_last_is_marked_as_its_end()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 1024 * 1024);
        await using var relay = FrameRelay.Start(master.ListenUri!);

        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        Pieces.ExpectSame(master, child);

        var blocks = relay.FromMaster.Where(frame => frame.Kind == FrameKindEnum.Blocks).ToList();

        Assert.True(blocks.Count >= 1024 * 1024 / Protocol.MaxDataBody, $"A root of 1 MiB arrived in {blocks.Count} Blocks frames.");
        Assert.All(relay.FromMaster, frame => Assert.True(frame.BodyLength <= Protocol.MaxDataBody, $"A {frame.Kind} frame has a body of {frame.BodyLength} bytes."));
        Assert.All(blocks[..^1], frame => Assert.False(frame.EndsABody, "A frame before the last of a root is marked as its end."));
        Assert.True(blocks[^1].EndsABody, "The last frame of a root is not marked as its end.");
    }

    [Fact]
    public async Task Small_roots_share_a_frame()
    {
        const int Roots = 64;
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, Roots, dataBytes: 16);
        await using var relay = FrameRelay.Start(master.ListenUri!);

        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        Pieces.ExpectSame(master, child);
        Assert.InRange(relay.FromMaster.Count(frame => frame.Kind == FrameKindEnum.Blocks), 1, Roots - 1);
    }

    [Fact]
    public async Task Schema_larger_than_a_frame_arrives_in_several_frames_and_the_child_has_it()
    {
        var schema = LargeSchema();
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);

        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        Assert.Equal(master.Schema.Fingerprint, child.Schema.Fingerprint);

        var frames = relay.FromMaster.Where(frame => frame.Kind == FrameKindEnum.Schema).ToList();

        Assert.True(frames.Count >= 2, $"A schema of more than one frame arrived in {frames.Count} Schema frames.");
        Assert.All(frames, frame => Assert.True(frame.BodyLength <= Protocol.MaxDataBody, $"A Schema frame has a body of {frame.BodyLength} bytes."));
        Assert.All(frames[..^1], frame => Assert.False(frame.EndsABody, "A frame before the last of the schema is marked as its end."));
        Assert.True(frames[^1].EndsABody, "The last frame of the schema is not marked as its end.");
    }

    // One entity type of 2,000 nullable Int64 fields with long names: its descriptor alone is several
    // times the largest body of a frame, and its fixed part, 16 KiB of fields, fits one block.
    private static LatticeSchema LargeSchema()
    {
        var fields = new StringBuilder("""{ "Id": 1, "Name": "Id", "Type": "Int64" }""");

        for (var index = 2; index <= 2_000; index++)
        {
            fields.Append($$""", { "Id": {{index}}, "Name": "Field_{{index:D4}}_with_a_name_long_enough_to_make_the_schema_large", "Type": "Int64", "Nullable": true }""");
        }

        return LatticeSchema.Compile($$"""{ "EntityTypes": [ { "Id": 1, "Name": "Wide", "Fields": [ {{fields}} ], "PrimaryKey": [1] } ] }""");
    }
}
