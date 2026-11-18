using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine;
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
// a descriptor whose kind does not match its length, an inline value whose location runs past the end of
// the root even where the sum of location and length wraps around — of a text and of a key (review round
// 1 of S10) —, a null bitmap that contradicts the descriptors, fewer or more bytes than the root
// announces, a key whose text is not well-formed, or fewer or more roots than LoadEnd announces fails its
// open at once with Unavailable and
// holds nothing; no received value is used as an address. So does a child whose schema, received in the
// Schema frames, is not the one whose fingerprint the HelloReply of its master announced (review round 1
// of S10).
//
// The master is real and holds one root that the test can find in a frame (MarkedRoot); a relay
// (FrameRelay) damages that one thing on the way, in the first Blocks frame, and sends the frame with a
// valid check, its flags kept, so that only the root is wrong — or it drops or repeats a whole Blocks
// frame, or announces another fingerprint in the HelloReply. The child's native memory is counted
// (CountingNativeAllocator), and its open timeout is far beyond the bound, so an open that ends within
// the bound ended at once.
public sealed class ReceivedRootTests
{
    // The key Code of the root is text that is not well-formed: four bytes that are no UTF-8, in a database
    // that stores UTF-8; a high surrogate with no low one after it, in a database that stores UTF-16 (review
    // round 1 of S11: a received key is checked as a Delete's key is).
    private const string KeyNotUtf8 = "a key that is not well-formed UTF-8";

    private const string KeyWithALoneSurrogate = "a key with a lone surrogate in UTF-16";

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("an unknown type id"),
        new("an unknown layout version"),
        new("a descriptor whose kind does not match its length"),
        new("an inline text whose location wraps around"),
        new("an inline key whose location wraps around"),
        new("a null bitmap that contradicts the descriptors"),
        new("fewer bytes than the root announces"),
        new("more bytes than the root announces"),
        new("no Blocks frame, fewer roots than LoadEnd announces"),
        new("a Blocks frame twice, more roots than LoadEnd announces"),
        new(KeyNotUtf8) { Skip = ReviewFindings.NotFixed },
        new(KeyWithALoneSurrogate) { Skip = ReviewFindings.NotFixed },
    ];

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task Child_that_receives_an_invalid_root_fails_its_open_at_once_with_Unavailable_and_holds_nothing(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(damage == KeyWithALoneSurrogate ? MarkedRoot.Utf16Schema : MarkedRoot.Schema);
        var marked = master.Set("Marked");
        var written = await marked.InsertAsync(MarkedRoot.RecordOf(marked.EntityType, "code"));
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

    [Fact]
    public async Task Child_whose_schema_is_not_the_one_the_HelloReply_announced_fails_its_open_at_once_with_Unavailable_and_holds_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedRoot.Schema);
        await master.Set("Marked").InsertAsync(MarkedRoot.Record());

        // The reply announces the fingerprint of another schema; the Schema frames carry the master's.
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.HelloReply)
            {
                return [frame];
            }

            var announced = FrameCodec.ReadHelloBody(new ReadOnlySequence<byte>(frame.Payload)) with { SchemaFingerprint = PieceSchema.Schema.Fingerprint };
            var output = new ArrayBufferWriter<byte>();

            FrameCodec.WriteHelloBody(output, in announced);
            return [frame.WithPayload(output.WrittenSpan)];
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

            case "an inline text whose location wraps around":
                WrapLocation(bytes, "Name");
                break;

            case "an inline key whose location wraps around":
                WrapLocation(bytes, "Code");
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

            case KeyNotUtf8:
                bytes.AsSpan(MarkedRoot.KeyAt(bytes, "code", Encoding.UTF8), 4).Fill(0xFF);
                break;

            case KeyWithALoneSurrogate:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(MarkedRoot.KeyAt(bytes, "code", Encoding.Unicode)), 0xD800);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return [frame.WithPayload(bytes)];
    }

    // The inline value of the field, 4 bytes long, gets the location 2^64 - 4 in its descriptor, after its
    // kind and its length: location and length add up to 2^64, which wraps around to 0.
    private static void WrapLocation(byte[] bytes, string field)
    {
        var descriptorAt = MarkedRoot.PayloadAt(bytes) + (MarkedRoot.FieldOf(field).Offset - BlockHeader.Size);

        Assert.Equal((uint)DescriptorKindEnum.Inline, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptorAt)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptorAt + 4)));

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(descriptorAt + 8), ulong.MaxValue - 3);
    }
}
