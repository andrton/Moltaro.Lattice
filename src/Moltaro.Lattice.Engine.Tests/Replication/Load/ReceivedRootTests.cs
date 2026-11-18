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

    // The texts that are not well-formed in a field that is not the key.
    private const string ChunkedUtf8 = "chunked UTF-8 that is not well-formed";

    private const string ChunkedUtf16OfOddLength = "chunked UTF-16 of an odd length";

    private const string InlineUtf16OfOddLength = "inline UTF-16 of an odd length";

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
        new(KeyNotUtf8),
        new(KeyWithALoneSurrogate),
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

    public static IEnumerable<TheoryDataRow<string>> TextsThatAreNotWellFormed =>
    [
        new(ChunkedUtf8),
        new(ChunkedUtf16OfOddLength),
        new(InlineUtf16OfOddLength),
    ];

    // A node checks the structure of what it receives, so that no read leaves the received bytes, and does
    // not validate the values of fields again (decision on replication and hosting,
    // docs/decisions/replication-and-hosting.md): a peer that sends text that is not well-formed can make a
    // child hold an entity whose read throws or shows replacement characters, never one that makes it read
    // outside its memory. So a child that receives such a text in a field that is not its key refuses the
    // root, its open failing with Unavailable and nothing held, or holds it; and a read of the field then
    // throws a managed exception or returns characters the received bytes hold, U+FFFD in the place of what
    // is not well-formed, and no more of them than there are bytes. The text is Note, of the character q:
    // in UTF-8, 70,000 bytes in two chunks, of which the relay makes bytes that no UTF-8 holds and the lead
    // byte of a character of three bytes at the end of every run of the text in a frame, so at the end of
    // the text and where a frame cuts it; in UTF-16, 10,000 bytes in a chunk, or 12 inline, whose last byte
    // the relay takes away, with the length of the text and of the root one byte shorter.
    [Theory]
    [MemberData(nameof(TextsThatAreNotWellFormed))]
    public async Task Child_that_receives_a_text_that_is_not_well_formed_refuses_it_or_reads_it_as_a_managed_failure_or_as_the_characters_it_received(string text)
    {
        var utf16 = text != ChunkedUtf8;
        var characters = text switch
        {
            ChunkedUtf8 => 70_000,
            ChunkedUtf16OfOddLength => 5_000,
            _ => 6,
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(utf16 ? MarkedRoot.Utf16Schema : MarkedRoot.Schema);
        var marked = master.Set("Marked");
        var record = MarkedRoot.RecordOf(marked.EntityType, "code");

        record["Note"] = new string('q', characters);
        await marked.InsertAsync(record);

        await using var relay = FrameRelay.Start(master.ListenUri!, frame => frame.Kind == FrameKindEnum.Blocks ? [NotWellFormed(frame, text)] : [frame]);
        var allocator = new CountingNativeAllocator();
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        var refusal = await Record.ExceptionAsync(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        if (refusal is not null)
        {
            Assert.Equal(LatticeErrorEnum.Unavailable, Assert.IsType<LatticeException>(refusal).Error);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
            allocator.ExpectEverythingReturned();

            return;
        }

        string? note = null;
        var failure = Record.Exception(() => note = child.Set("Marked").Get("code")?.Get<string>("Note"));

        if (failure is null)
        {
            Assert.NotNull(note);
            Assert.True(note.Length <= characters, $"The read returned {note.Length} characters of a text of {characters}.");
            Assert.True(note.All(character => character is 'q' or '�'), "The read returned a character that the received bytes do not hold.");
        }
    }

    // What the relay sends in the place of a Blocks frame of the root whose Note is the text of the row.
    private static WireFrame NotWellFormed(WireFrame frame, string text)
    {
        var bytes = frame.Payload;

        if (text == ChunkedUtf8)
        {
            foreach (var (start, length) in RunsOf(bytes, (byte)'q', least: 64))
            {
                bytes[start + 10] = 0xFF;
                bytes[start + length - 1] = 0xE2;
            }

            return frame.WithPayload(bytes);
        }

        // The one root of the frame: its count, its length and its bytes. Out of line, Note lies just before
        // Data, the last value, of 1,000 bytes; inline, it is the last value of the root payload, which
        // Data follows: either way its last byte is the one before Data.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        var rootLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(sizeof(uint)));
        var descriptorAt = MarkedRoot.PayloadAt(bytes) + (MarkedRoot.FieldOf("Note").Offset - BlockHeader.Size);
        var noteLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptorAt + 4));
        int lastByte;

        if (text == ChunkedUtf16OfOddLength)
        {
            Assert.Equal((uint)DescriptorKindEnum.Chunked, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptorAt)));
            lastByte = bytes.Length - 1_000 - 1;
        }
        else
        {
            Assert.Equal((uint)DescriptorKindEnum.Inline, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptorAt)));

            var location = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(descriptorAt + 8));

            lastByte = MarkedRoot.PayloadAt(bytes) + location - BlockHeader.Size + (int)noteLength - 1;
            Assert.Equal(bytes.Length - 1_000 - 1, lastByte);
        }

        Assert.Equal((uint)'q', BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lastByte - 1)));

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(descriptorAt + 4), noteLength - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(sizeof(uint)), rootLength - 1);

        return frame.WithPayload([.. bytes[..lastByte], .. bytes[(lastByte + 1)..]]);
    }

    // Where the runs of at least the given number of the byte begin in the bytes, and how long each is.
    private static List<(int Start, int Length)> RunsOf(byte[] bytes, byte value, int least)
    {
        var runs = new List<(int Start, int Length)>();

        for (var start = 0; start < bytes.Length;)
        {
            var end = start;

            while (end < bytes.Length && bytes[end] == value)
            {
                end++;
            }

            if (end - start >= least)
            {
                runs.Add((start, end - start));
            }

            start = Math.Max(end, start + 1);
        }

        return runs;
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
