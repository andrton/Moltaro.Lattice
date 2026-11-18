using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Replication;

// Writes and reads whole frames of the replication transport, and the bodies of the handshake (brief of
// Epic 1, sprint S10, "Types and members of S10"; the planner's decision of 17 November 2026 after the
// study, point 1). A frame is the header of Protocol.HeaderSize bytes (FrameHeader), the payload, and the
// check of Protocol.CheckSize bytes, the CRC32C of the header and of the payload (FrameCheck). The body
// length of the header counts the payload and the check, so a frame takes the header and its body
// length. Write takes the payload, TryRead returns it; neither hands out the check.
//
// Every number of the header, of the check and of the bodies written here is little-endian, so that the
// bytes are the same on every machine and a node of the other byte order can still read a Hello and be
// refused for its format.
//
// What TryRead checks, and when. From the header alone, as soon as its 32 bytes are there: the magic, the
// protocol version, the kind, the body length against the largest body of the kind
// (Protocol.MaxBodyOf), so that a frame that announces more is refused before anything is read or
// allocated for its body, and that no flag but Protocol.EndsABody is set. Then, once the whole frame is
// there, the check. Each failure is a
// LatticeException with ValidationFailed: the bytes are not a frame of this protocol, and nothing of
// them may be used. The order of the frames, their stream sequence, is checked by the connection that
// receives them (FrameConnection), and what a payload holds by the reader of its body.
internal static class FrameCodec
{
    // Writes a frame of the kind with no flag set; see the other Write.
    public static void Write(IBufferWriter<byte> output, FrameKindEnum kind, ulong correlationId, ulong streamSequence, ReadOnlySpan<byte> body) =>
        Write(output, kind, flags: 0, correlationId, streamSequence, body);

    // Writes a frame of the kind with the flags and the payload given, and its check. A payload larger
    // than the largest body of the kind leaves, and a kind that is not one, is a caller's mistake:
    // ArgumentOutOfRangeException, with nothing written.
    public static void Write(IBufferWriter<byte> output, FrameKindEnum kind, uint flags, ulong correlationId, ulong streamSequence, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(output);

        var bodyLength = payload.Length + Protocol.CheckSize;
        var maxBody = Protocol.MaxBodyOf(kind);

        if (maxBody == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of frame.");
        }

        if (bodyLength > maxBody)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"A {kind} frame holds at most {maxBody - Protocol.CheckSize} bytes of payload.");
        }

        var length = Protocol.HeaderSize + bodyLength;
        var frame = output.GetSpan(length)[..length];

        BinaryPrimitives.WriteUInt32LittleEndian(frame, Protocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], Protocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], (ushort)kind);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[12..], (uint)bodyLength);
        BinaryPrimitives.WriteUInt64LittleEndian(frame[16..], correlationId);
        BinaryPrimitives.WriteUInt64LittleEndian(frame[24..], streamSequence);
        payload.CopyTo(frame[Protocol.HeaderSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[^Protocol.CheckSize..], FrameCheck.Of(frame[..^Protocol.CheckSize]));

        output.Advance(length);
    }

    // Reads the frame at the position of the input: true with its header and its payload, the input
    // advanced past the frame; false when the input does not hold the whole frame yet, with nothing
    // consumed. ValidationFailed on a wrong magic, version, kind, body length or check (see the class).
    // The payload is a slice of the input and lives as long as its memory.
    public static bool TryRead(ref SequenceReader<byte> input, out FrameHeader header, out ReadOnlySequence<byte> body)
    {
        body = default;

        if (input.Remaining < Protocol.HeaderSize)
        {
            header = default;

            return false;
        }

        Span<byte> bytes = stackalloc byte[Protocol.HeaderSize];

        input.TryCopyTo(bytes);
        header = Parse(bytes);

        var length = Protocol.HeaderSize + (long)header.BodyLength;

        if (input.Remaining < length)
        {
            return false;
        }

        var frame = input.UnreadSequence.Slice(0, length);
        Span<byte> check = stackalloc byte[Protocol.CheckSize];

        frame.Slice(length - Protocol.CheckSize).CopyTo(check);

        if (FrameCheck.Of(frame.Slice(0, length - Protocol.CheckSize)) != BinaryPrimitives.ReadUInt32LittleEndian(check))
        {
            throw Invalid($"The check of a {header.Kind} frame does not match its bytes.");
        }

        body = frame.Slice(Protocol.HeaderSize, header.BodyLength - Protocol.CheckSize);
        input.Advance(length);

        return true;
    }

    // Writes the payload of a Hello or a HelloReply: the database identity in the order of
    // Guid.TryWriteBytes, the history, the 32 bytes of the fingerprint in their order, then the wire
    // format, the layout format, the version of the case table and the role, each an Int32.
    public static void WriteHelloBody(IBufferWriter<byte> output, in HelloBody hello)
    {
        ArgumentNullException.ThrowIfNull(output);

        var span = output.GetSpan(Protocol.HelloPayloadSize)[..Protocol.HelloPayloadSize];

        hello.DatabaseId.TryWriteBytes(span);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], hello.History);
        hello.SchemaFingerprint.WriteTo(span[24..56]);
        BinaryPrimitives.WriteInt32LittleEndian(span[56..], hello.WireFormat);
        BinaryPrimitives.WriteInt32LittleEndian(span[60..], hello.LayoutFormat);
        BinaryPrimitives.WriteInt32LittleEndian(span[64..], hello.CaseTableVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[68..], (int)hello.Role);

        output.Advance(Protocol.HelloPayloadSize);
    }

    // Reads the payload of a Hello or a HelloReply, as WriteHelloBody writes it. ValidationFailed when it
    // is not Protocol.HelloPayloadSize bytes long. Whether the values are acceptable is HelloCheck's.
    public static HelloBody ReadHelloBody(ReadOnlySequence<byte> body)
    {
        if (body.Length != Protocol.HelloPayloadSize)
        {
            throw Invalid($"The payload of a Hello has {Protocol.HelloPayloadSize} bytes, not {body.Length}.");
        }

        Span<byte> span = stackalloc byte[Protocol.HelloPayloadSize];

        body.CopyTo(span);

        return new HelloBody(
            new Guid(span[..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(span[16..]),
            new LatticeFingerprint(span[24..56]),
            BinaryPrimitives.ReadInt32LittleEndian(span[56..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[60..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[64..]),
            (LatticeRoleEnum)BinaryPrimitives.ReadInt32LittleEndian(span[68..]));
    }

    // Writes the payload of a Refuse: the reason as a UInt16.
    public static void WriteRefuseBody(IBufferWriter<byte> output, RefuseReasonEnum reason)
    {
        ArgumentNullException.ThrowIfNull(output);

        BinaryPrimitives.WriteUInt16LittleEndian(output.GetSpan(Protocol.RefusePayloadSize), (ushort)reason);
        output.Advance(Protocol.RefusePayloadSize);
    }

    // Reads the payload of a Refuse. ValidationFailed when it is not two bytes long or names no reason.
    public static RefuseReasonEnum ReadRefuseBody(ReadOnlySequence<byte> body)
    {
        if (body.Length != Protocol.RefusePayloadSize)
        {
            throw Invalid($"The payload of a Refuse has {Protocol.RefusePayloadSize} bytes, not {body.Length}.");
        }

        Span<byte> span = stackalloc byte[Protocol.RefusePayloadSize];

        body.CopyTo(span);

        var reason = (RefuseReasonEnum)BinaryPrimitives.ReadUInt16LittleEndian(span);

        return Enum.IsDefined(reason) ? reason : throw Invalid($"A Refuse names the reason {(ushort)reason}, which is none.");
    }

    // The fields of a header, checked as the class says.
    private static FrameHeader Parse(ReadOnlySpan<byte> bytes)
    {
        var header = new FrameHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            ProtocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
            Kind = (FrameKindEnum)BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            CorrelationId = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            StreamSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
        };

        if (header.Magic != Protocol.Magic)
        {
            throw Invalid($"A frame begins with 0x{header.Magic:X8}, not with the magic of the protocol.");
        }

        if (header.ProtocolVersion != Protocol.Version)
        {
            throw Invalid($"A frame has the protocol version {header.ProtocolVersion}, not {Protocol.Version}.");
        }

        var maxBody = Protocol.MaxBodyOf(header.Kind);

        if (maxBody == 0)
        {
            throw Invalid($"A frame has the kind {(ushort)header.Kind}, which is none.");
        }

        if (header.BodyLength < Protocol.CheckSize || header.BodyLength > maxBody)
        {
            throw Invalid($"A {header.Kind} frame announces a body of {header.BodyLength} bytes; it has {Protocol.CheckSize} to {maxBody}.");
        }

        if ((header.Flags & ~Protocol.EndsABody) != 0)
        {
            throw Invalid($"A {header.Kind} frame has the flags 0x{header.Flags:X8}; only bit 0 is defined.");
        }

        return header;
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);
}
