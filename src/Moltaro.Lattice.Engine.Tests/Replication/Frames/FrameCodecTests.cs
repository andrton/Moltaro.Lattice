using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Frames;

// The codec of the frames of the replication transport, without a network (brief of Epic 1, sprint S10,
// "Types and members of S10"; the planner's decision of 17 November 2026 after the study, point 1):
// - a frame written by FrameCodec.Write is read back by FrameCodec.TryRead with its header and its
//   payload, also when it arrives in two pieces, and two frames written one after the other are read in
//   their order;
// - TryRead asks for more bytes, and consumes nothing, for every prefix of a frame;
// - a wrong magic, another protocol version, an unknown kind, a body length over the largest of its kind,
//   a damaged check and a damaged body each fail with ValidationFailed, and a body length is refused
//   from the header alone, before anything is allocated for the body it announces;
// - the header is 32 bytes, and the bytes of a header, of its check and of a Hello are the same on every
//   machine: little-endian, given here byte for byte. The check is the CRC32C of the header and of the
//   body before it (Crc32C);
// - every reason of a refusal reads back as written.
// Frames are made and damaged apart from the codec (WireFrame), in the form the brief gives and with the
// assumptions WireFrame states.
public sealed class FrameCodecTests
{
    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("a wrong magic"),
        new("another protocol version"),
        new("an unknown kind"),
        new("a body length over the largest of its kind"),
        new("a damaged check"),
        new("a damaged body"),
    ];

    [Fact]
    public void Frame_written_by_the_codec_is_read_back_with_its_header_and_its_payload()
    {
        var payload = PayloadOf(1_000);
        var output = new ArrayBufferWriter<byte>();

        FrameCodec.Write(output, FrameKindEnum.Blocks, correlationId: 7, streamSequence: 9, payload);
        FrameCodec.Write(output, FrameKindEnum.LoadEnd, correlationId: 8, streamSequence: 10, ReadOnlySpan<byte>.Empty);

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(output.WrittenMemory));

        Assert.True(FrameCodec.TryRead(ref reader, out var first, out var firstPayload));
        Assert.Equal(Protocol.Magic, first.Magic);
        Assert.Equal(Protocol.Version, first.ProtocolVersion);
        Assert.Equal(FrameKindEnum.Blocks, first.Kind);
        Assert.Equal(0u, first.Flags);
        Assert.Equal((uint)(payload.Length + Protocol.CheckSize), first.BodyLength);
        Assert.Equal(7UL, first.CorrelationId);
        Assert.Equal(9UL, first.StreamSequence);
        Assert.Equal(payload, firstPayload.ToArray());

        Assert.True(FrameCodec.TryRead(ref reader, out var second, out var secondPayload));
        Assert.Equal(FrameKindEnum.LoadEnd, second.Kind);
        Assert.Equal(10UL, second.StreamSequence);
        Assert.Equal(0, secondPayload.Length);
        Assert.True(reader.End);
    }

    [Fact]
    public void Frame_that_arrives_in_two_pieces_is_read_whole_wherever_it_is_cut()
    {
        var frame = Written(FrameKindEnum.Blocks, PayloadOf(100));

        for (var cut = 1; cut < frame.Length; cut++)
        {
            var reader = new SequenceReader<byte>(InTwoPieces(frame, cut));

            Assert.True(FrameCodec.TryRead(ref reader, out var header, out var payload), $"A frame cut after {cut} bytes was not read.");
            Assert.Equal(FrameKindEnum.Blocks, header.Kind);
            Assert.Equal(PayloadOf(100), payload.ToArray());
        }
    }

    [Fact]
    public void Codec_asks_for_more_bytes_and_consumes_none_for_every_prefix_of_a_frame()
    {
        var frame = Written(FrameKindEnum.Blocks, PayloadOf(100));

        for (var length = 0; length < frame.Length; length++)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame, 0, length));

            Assert.False(FrameCodec.TryRead(ref reader, out _, out _), $"A prefix of {length} of the {frame.Length} bytes of a frame was read as a frame.");
            Assert.Equal(0, reader.Consumed);
        }
    }

    [Theory]
    [MemberData(nameof(Damages))]
    public void Damaged_frame_fails_with_ValidationFailed(string damage)
    {
        var frame = new WireFrame(Written(FrameKindEnum.Blocks, PayloadOf(100)));
        var damaged = damage switch
        {
            "a wrong magic" => frame.WithMagic(0x0BADF00D),
            "another protocol version" => frame.WithProtocolVersion((ushort)(Protocol.Version + 1)),
            "an unknown kind" => frame.WithKind(999),
            "a body length over the largest of its kind" => frame.WithBodyLength(Protocol.MaxDataBody + 1),
            "a damaged check" => frame.WithDamagedCheck(),
            "a damaged body" => frame.WithDamagedPayload(),
            _ => throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage."),
        };

        ExpectValidationFailed(damaged.Bytes);
    }

    [Fact]
    public void Body_length_over_the_largest_is_refused_from_the_header_alone_before_anything_is_allocated_for_it()
    {
        var header = WireFrame.Make(Protocol.Magic, Protocol.Version, (ushort)FrameKindEnum.Blocks, 0, uint.MaxValue, 0, 0, []).Bytes[..WireFrame.HeaderSize];

        // A read of a valid frame first, so that nothing the first call of the codec loads is counted.
        _ = Read(Written(FrameKindEnum.Blocks, PayloadOf(10)));
        var before = GC.GetAllocatedBytesForCurrentThread();

        ExpectValidationFailed(header);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < Protocol.MaxDataBody, $"Refusing a body length of {uint.MaxValue} bytes allocated {allocated} bytes.");
    }

    [Fact]
    public void Header_of_a_frame_is_32_bytes()
    {
        Assert.Equal(32, Unsafe.SizeOf<FrameHeader>());
        Assert.Equal(32 + Protocol.CheckSize, Written(FrameKindEnum.LoadEnd, []).Length);
    }

    [Fact]
    public void Header_and_check_of_a_frame_are_the_same_little_endian_bytes_on_every_machine()
    {
        byte[] header =
        [
            0x4C, 0x41, 0x54, 0x54,                                 // the magic, the bytes "LATT"
            0x01, 0x00,                                             // the protocol version, 1
            0x03, 0x00,                                             // the kind, Schema
            0x00, 0x00, 0x00, 0x00,                                 // no flag
            0x07, 0x00, 0x00, 0x00,                                 // the body length: three bytes and the check
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,         // the correlation id
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,         // the stream sequence
            0xAA, 0xBB, 0xCC,                                       // the payload
        ];
        var check = new byte[Protocol.CheckSize];
        BinaryPrimitives.WriteUInt32LittleEndian(check, Crc32C.Of(header));
        var output = new ArrayBufferWriter<byte>();

        FrameCodec.Write(output, FrameKindEnum.Schema, 0x0102030405060708, 0x1112131415161718, [0xAA, 0xBB, 0xCC]);

        Assert.Equal([.. header, .. check], output.WrittenSpan.ToArray());
    }

    [Fact]
    public void Check_of_a_frame_is_the_CRC32C_of_its_header_and_its_body_before_it()
    {
        // The check value of CRC-32C, which pins the variant of the reference.
        Assert.Equal(0xE3069283u, Crc32C.Of(Encoding.ASCII.GetBytes("123456789")));

        var frame = new WireFrame(Written(FrameKindEnum.Blocks, PayloadOf(1_000)));

        Assert.Equal(Crc32C.Of(frame.Bytes.AsSpan(0, frame.Bytes.Length - Protocol.CheckSize)), frame.Check);
    }

    [Fact]
    public void Hello_body_is_the_same_little_endian_bytes_on_every_machine_and_reads_back_as_written()
    {
        var fingerprint = LatticeSchema.Compile("""{ "EntityTypes": [ { "Id": 1, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }""").Fingerprint;
        var hello = new HelloBody(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            0x0102030405060708,
            fingerprint,
            WireFormat: 0x01020304,
            LayoutFormat: 0x05060708,
            CaseTableVersion: 1,
            LatticeRoleEnum.Child);
        byte[] expected =
        [
            0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66,         // the database identity, in the order of Guid.TryWriteBytes
            0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,         // the history
            .. Convert.FromHexString(fingerprint.ToString()),       // the 32 bytes of the fingerprint, in their order
            0x04, 0x03, 0x02, 0x01,                                 // the wire format
            0x08, 0x07, 0x06, 0x05,                                 // the layout format
            0x01, 0x00, 0x00, 0x00,                                 // the version of the case table
            0x01, 0x00, 0x00, 0x00,                                 // the role, Child
        ];
        var output = new ArrayBufferWriter<byte>();

        FrameCodec.WriteHelloBody(output, in hello);

        Assert.Equal(expected, output.WrittenSpan.ToArray());
        Assert.Equal(hello, FrameCodec.ReadHelloBody(new ReadOnlySequence<byte>(expected)));
    }

    [Fact]
    public void Every_reason_of_a_refusal_reads_back_as_written()
    {
        foreach (var reason in Enum.GetValues<RefuseReasonEnum>())
        {
            var output = new ArrayBufferWriter<byte>();

            FrameCodec.WriteRefuseBody(output, reason);

            Assert.Equal(reason, FrameCodec.ReadRefuseBody(new ReadOnlySequence<byte>(output.WrittenMemory)));
        }
    }

    // Bytes that do not repeat within a few hundred, so that a byte read from the wrong place shows.
    private static byte[] PayloadOf(int length) => [.. Enumerable.Range(0, length).Select(index => (byte)((index * 7) + 3))];

    private static byte[] Written(FrameKindEnum kind, byte[] payload)
    {
        var output = new ArrayBufferWriter<byte>();

        FrameCodec.Write(output, kind, correlationId: 1, streamSequence: 1, payload);
        return output.WrittenSpan.ToArray();
    }

    private static bool Read(byte[] bytes)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));

        return FrameCodec.TryRead(ref reader, out _, out _);
    }

    private static void ExpectValidationFailed(byte[] bytes)
    {
        var exception = Assert.Throws<LatticeException>(() => Read(bytes));

        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
    }

    // The bytes as a sequence of two segments, the first of the bytes before the cut.
    private static ReadOnlySequence<byte> InTwoPieces(byte[] bytes, int cut)
    {
        var first = new Piece(bytes.AsMemory(0, cut), runningIndex: 0);
        var second = first.Append(bytes.AsMemory(cut));

        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private sealed class Piece : ReadOnlySequenceSegment<byte>
    {
        public Piece(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Piece Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Piece(memory, RunningIndex + Memory.Length);

            Next = next;
            return next;
        }
    }
}
