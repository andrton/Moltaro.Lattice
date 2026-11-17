using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// What a child receives of a root is checked before it is used (brief of Epic 1, sprint S10, the
// planner's decision of 17 November 2026 after the study, points 2 and 3): the type id and the layout
// version against the schema, the kind of each descriptor against its length, the null bitmap against
// the descriptors, the total of the bytes against the announced length of the root; and the number of
// roots against the count LoadEnd announces. A child that receives an unknown type id or layout version,
// a descriptor whose kind does not match its length, a null bitmap that contradicts the descriptors,
// fewer or more bytes than the root announces, or fewer or more roots than LoadEnd announces fails its
// open at once with Unavailable and holds nothing; no received value is used as an address.
//
// The master is real and holds one root that the test can find in a frame (MarkedRoot); a relay
// (FrameRelay) damages that one thing on the way, in the first Blocks frame, and sends the frame with a
// valid check, its flags kept, so that only the root is wrong — or it drops or repeats a whole Blocks
// frame. The child's native memory is counted (CountingNativeAllocator), and its open timeout is far
// beyond the bound, so an open that ends within the bound ended at once.
public sealed class ReceivedRootTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("an unknown type id"),
        new("an unknown layout version"),
        new("a descriptor whose kind does not match its length"),
        new("a null bitmap that contradicts the descriptors"),
        new("fewer bytes than the root announces"),
        new("more bytes than the root announces"),
        new("no Blocks frame, fewer roots than LoadEnd announces"),
        new("a Blocks frame twice, more roots than LoadEnd announces"),
    ];

    [Theory(Skip = ChildLoading.NotImplemented)]
    [MemberData(nameof(Damages))]
    public async Task Child_that_receives_an_invalid_root_fails_its_open_at_once_with_Unavailable_and_holds_nothing(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedRoot.Schema);
        var written = await master.Set("Marked").InsertAsync(MarkedRoot.Record());
        var lastMutation = PositionSequence.Of(written.Position);
        var damagedOne = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Blocks || damagedOne)
            {
                return [frame];
            }

            damagedOne = true;
            return Damaged(frame, damage, lastMutation);
        });
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();
    }

    // What the relay sends in the place of the first Blocks frame, which holds the one root.
    private static IEnumerable<WireFrame> Damaged(WireFrame frame, string damage, ulong lastMutation)
    {
        var bytes = frame.Payload;

        switch (damage)
        {
            case "an unknown type id":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(MarkedRoot.TypeIdAt(bytes)), MarkedRoot.TypeId + 1);
                break;

            case "an unknown layout version":
                var typeIdAt = MarkedRoot.TypeIdAt(bytes);

                // The last mutation sequence follows the type id; that it is there confirms the widths the
                // test assumes for the envelope of a root.
                Assert.True(
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(typeIdAt + 4)) == lastMutation,
                    "The last mutation sequence of the root does not follow its type id: the envelope of a root is not the one the test assumes (MarkedRoot).");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(typeIdAt + 12), 999);
                break;

            case "a descriptor whose kind does not match its length":
                // Name is inline and 4 bytes long; a chunked value is longer than a block of its own holds.
                var descriptorAt = MarkedRoot.PayloadAt(bytes) + (MarkedRoot.FieldOf("Name").Offset - BlockHeader.Size);

                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(descriptorAt), (uint)DescriptorKindEnum.Chunked);
                break;

            case "a null bitmap that contradicts the descriptors":
                // Note is not null, and its descriptor says where its value is; its bit says it is null.
                var noteBit = MarkedRoot.FieldOf("Note").NullBit;

                bytes[MarkedRoot.PayloadAt(bytes) + (noteBit / 8)] |= (byte)(1 << (noteBit % 8));
                break;

            case "fewer bytes than the root announces":
                bytes = bytes[..^1];
                break;

            case "more bytes than the root announces":
                bytes = [.. bytes, 0];
                break;

            case "no Blocks frame, fewer roots than LoadEnd announces":
                return [];

            case "a Blocks frame twice, more roots than LoadEnd announces":
                return [frame, frame];

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return [frame.WithPayload(bytes)];
    }
}
