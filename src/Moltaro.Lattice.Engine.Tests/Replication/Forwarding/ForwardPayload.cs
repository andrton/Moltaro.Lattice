using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The frames of a write forwarded through a child as the tests of Epic 1, sprint S12 assume them, apart
// from the codec of the engine (Replication.ForwardEncoding): WriteRequest from a child to its master and
// WriteReply from the master back. The implementer of S12 builds to these shapes. They follow the brief of
// S12 (the planner's block of 15 November 2026, "Forwarding had no frames in the brief", and his decisions
// of 18 November 2026, points 1, 3, 6, 7 and 13); where the brief leaves a shape open, the simplest is
// chosen here, as S10 and S11 chose theirs: little-endian, a UInt64 for a sequence and a length, a UInt32
// for a type id and a text length, one byte for a kind and for a mark of presence.
//
// The frames.
// - FrameKindEnum.WriteRequest = 10 and FrameKindEnum.WriteReply = 11 (brief of S12), whose largest body
//   is Protocol.MaxDataBody (WriteRequest, WriteReply below).
// - Every frame of a request carries the id of the request in FrameHeader.CorrelationId: ids start at 1
//   on each connection and grow by one with each request the sender of the child takes, in that order;
//   0 stays the load and the stream. Every frame of a reply carries the id of its request.
// - A request travels as its announced length, the length of its body (UInt64), followed by its body; a
//   reply the same way. These bytes are cut into frames in their order, each payload at most
//   Protocol.MaxDataPayload, the first beginning with the announced length; bit 0 of the flags
//   (Protocol.EndsABody) is set on the last frame and on no other. A sender may cut the bytes shorter;
//   the receiver takes any cut. The frames of one request are not interleaved with those of another
//   request, and Applied frames may pass between them; a reply may come anywhere in the stream of the
//   master, also between the frames of a transaction.
// - FrameKindEnum.Closed = 12 (decided at the review of S12, point 2; Closed below): from a master that
//   closes in order, once per connection, after the replies and the tail it sends there and before the
//   end of its sending side. It has no body: its payload is empty, so its frame is a header and the check,
//   and its correlation id is 0. Every request of the connection not answered before it was not executed.
//
// The body of a request.
// - Its kind (one byte): 0 Insert, 1 Replace, 2 Upsert, 3 Delete, the values of Access.WriteKindEnum.
// - The type id of its entity type (UInt32).
// - The expected revision: 0, or 1 and the two UInt64 of LatticeRevision in the order it holds them.
// - The minimum position: 0, or 1, its history (UInt64) and its sequence (UInt64).
// - The key, in the form of the key of a Delete of S11 (Engine.Entities.EntityTable.WriteKey): an
//   integer key as an Int64, a Guid key as its 16 bytes, a String key as the length of its UTF-8 (UInt32)
//   and that UTF-8.
// - Except for a Delete, which ends with its key: the length of the root (UInt64) and the root in the
//   load format of S10 (decided after the study of S12, point 1), as a Blocks frame carries it — type id
//   (UInt32), last mutation sequence (UInt64), which the child writes as 0, layout version (UInt32), root
//   payload, values out of line.
//
// The body of a reply.
// - 0 and a result: its position, history (UInt64) and sequence (UInt64), and Changed (one byte, 0 or 1).
// - 1 and a refusal: the error (Int32, LatticeErrorEnum), the message (the length of its UTF-8, UInt32,
//   and that UTF-8), Limit (0, or 1 and LatticeLimitEnum as an Int32), OperationIndex (0, or 1 and an
//   Int32), CommittedPosition (0, or 1, its history and its sequence) and, from the wire format 4, the
//   unique index (0, or 1 and the identity of the index, an Int32) of UniqueConstraintViolation.
internal static class ForwardPayload
{
    // FrameKindEnum.WriteRequest and FrameKindEnum.WriteReply of the brief of S12.
    public const FrameKindEnum WriteRequest = FrameKindEnum.WriteRequest;

    public const FrameKindEnum WriteReply = FrameKindEnum.WriteReply;

    public const FrameKindEnum Closed = FrameKindEnum.Closed;

    // The kinds of a request: the values of WriteKindEnum.
    public const byte Insert = 0;

    public const byte Replace = 1;

    public const byte Upsert = 2;

    public const byte Delete = 3;

    // The kind of a write batch, the value after those of WriteKindEnum, from the wire format 5.
    public const byte Batch = 4;

    // Where the last mutation sequence of a root stands: after its type id.
    private const int LastMutationAt = sizeof(uint);

    // The body of a request without a revision and a minimum position unless they are given; the root
    // is left out for a Delete.
    public static byte[] Request(byte kind, uint typeId, ReadOnlySpan<byte> key, ReadOnlySpan<byte> root, LatticePosition? minimumPosition = null, LatticeRevision? expectedRevision = null)
    {
        var body = new List<byte> { kind };

        AddUInt32(body, typeId);

        if (expectedRevision is { } revision)
        {
            // The two values of the revision, from its text: both in hexadecimal, 16 digits each, in the
            // order the revision holds them (LatticeRevision.ToString).
            var text = revision.ToString();

            body.Add(1);
            AddUInt64(body, ulong.Parse(text.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            AddUInt64(body, ulong.Parse(text.AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        else
        {
            body.Add(0);
        }

        if (minimumPosition is { } position)
        {
            body.Add(1);
            AddUInt64(body, HistoryOf(position));
            AddUInt64(body, PositionSequence.Of(position));
        }
        else
        {
            body.Add(0);
        }

        body.AddRange(key);

        if (kind != Delete)
        {
            AddUInt64(body, (ulong)root.Length);
            body.AddRange(root);
        }

        return [.. body];
    }

    // The body of a write batch, from the wire format 5 (the brief of Epic 1, sprint S17, point 7, in the
    // shapes its internal contract gives): the kind Batch, the minimum position as a request carries it, none
    // unless given, the count of the members (UInt32), the members' own unless given, and each member in its
    // order — its kind, the type id of its entity type, the expected revision (0 here), its key and, but for a
    // Delete, the length of its root (UInt64) and the root, so that a member ends where its length says.
    public static byte[] BatchRequest(IReadOnlyList<(byte Kind, uint TypeId, byte[] Key, byte[] Root)> members, uint? count = null, ulong? rootLength = null, LatticePosition? minimumPosition = null)
    {
        var body = new List<byte> { Batch };

        if (minimumPosition is { } position)
        {
            body.Add(1);
            AddUInt64(body, HistoryOf(position));
            AddUInt64(body, PositionSequence.Of(position));
        }
        else
        {
            body.Add(0);
        }

        AddUInt32(body, count ?? (uint)members.Count);

        foreach (var (kind, typeId, key, root) in members)
        {
            body.Add(kind);
            AddUInt32(body, typeId);
            body.Add(0);
            body.AddRange(key);

            if (kind != Delete)
            {
                AddUInt64(body, rootLength ?? (ulong)root.Length);
                body.AddRange(root);
            }
        }

        return [.. body];
    }

    // A String key as a request carries it.
    public static byte[] KeyOf(string key)
    {
        var text = Encoding.UTF8.GetBytes(key);
        var bytes = new byte[sizeof(uint) + text.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)text.Length);
        text.CopyTo(bytes, sizeof(uint));

        return bytes;
    }

    // An Int64 key as a request carries it.
    public static byte[] KeyOf(long key)
    {
        var bytes = new byte[sizeof(long)];

        BinaryPrimitives.WriteInt64LittleEndian(bytes, key);

        return bytes;
    }

    // The root of the one root of a Blocks frame of a load, as a child sends it in a request: its last
    // mutation sequence 0.
    public static byte[] RootOf(WireFrame blocks)
    {
        Assert.Equal(FrameKindEnum.Blocks, blocks.Kind);

        var payload = blocks.Payload;

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(payload));

        var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(uint)));
        var root = payload.AsSpan(sizeof(uint) + sizeof(ulong), length).ToArray();

        BinaryPrimitives.WriteUInt64LittleEndian(root.AsSpan(LastMutationAt), 0);

        return root;
    }

    // The frames of a request or a reply of the kind with the body, from the stream sequence given,
    // cut into frames of the most bytes given: its announced length, unless the test gives another, then
    // its body, the last frame marked.
    public static List<WireFrame> Frames(FrameKindEnum kind, ulong correlationId, ulong firstStreamSequence, ReadOnlySpan<byte> body, ulong? announcedLength = null, int framePayload = Protocol.MaxDataPayload)
    {
        var bytes = new byte[sizeof(ulong) + body.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, announcedLength ?? (ulong)body.Length);
        body.CopyTo(bytes.AsSpan(sizeof(ulong)));

        var frames = new List<WireFrame>();

        for (var offset = 0; offset < bytes.Length; offset += framePayload)
        {
            var part = bytes.AsSpan(offset, Math.Min(framePayload, bytes.Length - offset));
            var last = offset + part.Length == bytes.Length;

            frames.Add(WireFrame.Make(kind, last ? Protocol.EndsABody : 0, correlationId, firstStreamSequence + (ulong)frames.Count, part));
        }

        return frames;
    }

    // The body of a request or a reply from its frames, in their order: what the announced length of the
    // first announces, which the frames hold exactly, the last one marked and no other.
    public static byte[] BodyOf(IReadOnlyList<WireFrame> frames)
    {
        var bytes = frames.SelectMany(frame => frame.Payload).ToArray();
        var announced = BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        Assert.Equal((ulong)bytes.Length - sizeof(ulong), announced);
        Assert.All(frames.SkipLast(1), frame => Assert.False(frame.EndsABody));
        Assert.True(frames[^1].EndsABody);

        return bytes[sizeof(ulong)..];
    }

    // The frame Closed at the place given of the stream of the master.
    public static WireFrame ClosedFrame(ulong streamSequence) => WireFrame.Make(Closed, flags: 0, correlationId: 0, streamSequence, []);

    // The body of a reply with a result of the values given.
    public static byte[] Result(LatticePosition position, bool changed)
    {
        var body = new List<byte> { 0 };

        AddUInt64(body, HistoryOf(position));
        AddUInt64(body, PositionSequence.Of(position));
        body.Add(changed ? (byte)1 : (byte)0);

        return [.. body];
    }

    // The body of a reply with a refusal of the values given, which names the unique index of the identity
    // given, or none.
    public static byte[] Refusal(LatticeErrorEnum error, string message, LatticeLimitEnum? limit, int? operationIndex, LatticePosition? committedPosition, int? indexId = null)
    {
        var body = new List<byte> { 1 };

        AddUInt32(body, (uint)error);
        AddUInt32(body, (uint)Encoding.UTF8.GetByteCount(message));
        body.AddRange(Encoding.UTF8.GetBytes(message));
        AddOptional(body, limit is { } value ? (uint)value : null);
        AddOptional(body, operationIndex is { } index ? (uint)index : null);

        if (committedPosition is { } position)
        {
            body.Add(1);
            AddUInt64(body, HistoryOf(position));
            AddUInt64(body, PositionSequence.Of(position));
        }
        else
        {
            body.Add(0);
        }

        AddOptional(body, indexId is { } id ? (uint)id : null);

        return [.. body];
    }

    // What a reply of the body says: a result, or a refusal as the exception the child throws for it.
    public static Reply ReplyOf(byte[] body)
    {
        var reader = new BodyReader(body);

        if (reader.Byte() == 0)
        {
            var position = reader.Position();

            return new Reply(position, reader.Byte() == 1, null);
        }

        var error = (LatticeErrorEnum)reader.UInt32();
        var message = reader.Text();
        var limit = reader.Byte() == 1 ? (LatticeLimitEnum?)reader.UInt32() : null;
        var operationIndex = reader.Byte() == 1 ? (int?)reader.UInt32() : null;
        var committed = reader.Byte() == 1 ? (LatticePosition?)reader.Position() : null;
        var indexId = reader.Byte() == 1 ? (int?)reader.UInt32() : null;

        return new Reply(null, false, new LatticeException(error, message) { Limit = limit, OperationIndex = operationIndex, CommittedPosition = committed }, indexId);
    }

    // The history of a position, which the contract keeps internal, from its text: the history, a hyphen
    // and the sequence, both in hexadecimal (LatticePosition.ToString; TestSupport, PositionSequence).
    public static ulong HistoryOf(LatticePosition position)
    {
        var text = position.ToString();

        return ulong.Parse(text.AsSpan(0, text.IndexOf('-', StringComparison.Ordinal)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static LatticePosition PositionOf(ulong history, ulong sequence) =>
        LatticePosition.Parse(string.Create(CultureInfo.InvariantCulture, $"{history:x16}-{sequence:x}"));

    private static void AddOptional(List<byte> body, uint? value)
    {
        if (value is { } present)
        {
            body.Add(1);
            AddUInt32(body, present);
        }
        else
        {
            body.Add(0);
        }
    }

    private static void AddUInt32(List<byte> body, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        body.AddRange(bytes);
    }

    private static void AddUInt64(List<byte> body, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        body.AddRange(bytes);
    }

    // A reply as the tests read it: the position and Changed of a result, or the refusal, with the identity
    // of the unique index it names.
    public sealed record Reply(LatticePosition? Position, bool Changed, LatticeException? Refusal, int? IndexId = null);

    // Reads the fields of a body in their order; a body that ends early fails the test.
    private sealed class BodyReader(byte[] body)
    {
        private int _offset;

        public byte Byte() => body[_offset++];

        public uint UInt32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(_offset));

            _offset += sizeof(uint);

            return value;
        }

        public ulong UInt64()
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(_offset));

            _offset += sizeof(ulong);

            return value;
        }

        public string Text()
        {
            var length = (int)UInt32();
            var text = Encoding.UTF8.GetString(body, _offset, length);

            _offset += length;

            return text;
        }

        public LatticePosition Position()
        {
            var history = UInt64();

            return PositionOf(history, UInt64());
        }
    }
}
