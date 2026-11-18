using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// Text of a Dictionary field travels with its root as bytes, and the child checks its form before it uses
// it (brief of Epic 1, sprint S13, the planner's decision after the study, point 8: for a Dictionary field a
// Text descriptor with location 0 and its bytes out of line; the child checks that form and builds its
// own entries; no transfer ids, the wire format unchanged):
// - a master sends the text of a Dictionary field so: the kind Text, the length of the text, the location
//   0, and the bytes of the text out of line, before the values out of line of later descriptors;
// - a child that receives a Dictionary text in another form — a Text descriptor with a location that is
//   not 0, the text in the form of a Plain value, a Text descriptor in a Plain field, a Text descriptor
//   that announces more bytes than the root holds, a root without the bytes of its text, a Text descriptor
//   of the empty text — fails its open at once with Unavailable and holds nothing, as for every root that
//   is not valid (ReceivedRootTests): no entry is made and no native memory is left.
// The master is real and holds one root that the test can find in a frame (MarkedTextRoot); a relay
// (FrameRelay) damages that root in the first Blocks frame and sends the frame with a valid check. The
// child's native memory is counted (CountingNativeAllocator), and its open timeout is far beyond the bound,
// so an open that ends within the bound ended at once.
public sealed class ReceivedTextTests
{
    // In a Blocks frame of one root, the length of the root follows the count of the roots (RootFramer).
    private const int RootLengthAt = sizeof(uint);

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    public static IEnumerable<TheoryDataRow<string>> Damages => MarkedTextRoot.Damages;

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public async Task Master_sends_a_Dictionary_text_as_a_Text_descriptor_with_location_0_and_its_bytes_out_of_line()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedTextRoot.Schema);
        await master.Set("Regional").InsertAsync(MarkedTextRoot.RecordOf(master.Set("Regional").EntityType, "code"));
        await using var relay = FrameRelay.Start(master.ListenUri!);

        await using (var child = await ReplicationNodes.OpenChildAsync(relay.Address))
        {
            Assert.Equal(MarkedTextRoot.RegionText, child.Set("Regional").Get("code")?["Region"]);
        }

        var bytes = relay.FromMaster.First(frame => frame.Kind == FrameKindEnum.Blocks).Payload;
        var region = MarkedTextRoot.DescriptorAt(bytes, "Region");
        var text = Encoding.UTF8.GetBytes(MarkedTextRoot.RegionText);

        Assert.Equal((uint)DescriptorKindEnum.Text, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(region)));
        Assert.Equal((uint)text.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(region + 4)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(region + 8)));

        // The values out of line follow the root payload in the order of their descriptors: the text, then
        // the 1,000 bytes of Data, which end the root and the frame.
        var textAt = MarkedRoot.OnlyPlaceOf(bytes, text, "the text");

        Assert.Equal(bytes.Length - 1_000 - text.Length, textAt);
    }

    [Theory(Skip = SkipReason.BeforeSprint13)]
    [MemberData(nameof(Damages))]
    public async Task Child_that_receives_a_Dictionary_text_in_another_form_fails_its_open_at_once_with_Unavailable_and_holds_nothing(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedTextRoot.Schema);
        var texts = master.Set("Regional");
        await texts.InsertAsync(MarkedTextRoot.RecordOf(texts.EntityType, "code", MarkedTextRoot.RegionFor(damage)));
        var damagedOne = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Blocks || damagedOne)
            {
                return [frame];
            }

            damagedOne = true;
            return [frame.WithPayload(MarkedTextRoot.Damaged(frame.Payload, damage, RootLengthAt))];
        });
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();
    }
}
