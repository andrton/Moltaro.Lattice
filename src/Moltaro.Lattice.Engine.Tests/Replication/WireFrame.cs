using System.Buffers.Binary;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// One frame of the replication transport as its bytes, for the tests that read, make and damage frames
// apart from the codec of the engine. The form is the one the brief of Epic 1, sprint S10 and the
// planner's decision of 17 November 2026 after its study give: a header of 32 bytes in little-endian
// order — magic (4), protocol version (2), kind (2), flags (4), body length (4), correlation id (8),
// stream sequence (8) — and then the body, whose last bytes are the check: the CRC32C (Crc32C) of the
// header and of the body before it, 4 bytes, little-endian. Bit 0 of the flags marks the last frame of a
// root. Where the brief leaves it open the tests assume that the body length counts the check with the
// body, as MaxDataBody includes the check, so that a frame takes 32 bytes and its body length. The
// payload is the body without its check: what FrameCodec.Write is given and FrameCodec.TryRead returns.
//
// The With… members return a copy with one thing wrong and, unless the check is the thing, a check made
// again for what the copy holds, so that only that thing is wrong.
internal sealed class WireFrame
{
    public const int HeaderSize = 32;

    // The bit of the flags that marks the last frame of a root.
    public const uint EndsARootFlag = 1;

    public WireFrame(byte[] bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes.Length, HeaderSize + Protocol.CheckSize);

        Bytes = bytes;
    }

    public byte[] Bytes { get; }

    public uint Magic => BinaryPrimitives.ReadUInt32LittleEndian(Bytes);

    public ushort ProtocolVersion => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(4));

    public FrameKindEnum Kind => (FrameKindEnum)BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(6));

    public uint Flags => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8));

    public uint BodyLength => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(12));

    public ulong CorrelationId => BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(16));

    public ulong StreamSequence => BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(24));

    public byte[] Payload => Bytes[HeaderSize..^Protocol.CheckSize];

    public uint Check => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(Bytes.Length - Protocol.CheckSize));

    public bool EndsARoot => (Flags & EndsARootFlag) != 0;

    // A valid frame of the payload.
    public static WireFrame Make(FrameKindEnum kind, uint flags, ulong correlationId, ulong streamSequence, ReadOnlySpan<byte> payload) =>
        Make(Protocol.Magic, Protocol.Version, (ushort)kind, flags, (uint)(payload.Length + Protocol.CheckSize), correlationId, streamSequence, payload);

    // A frame of the header fields and the payload given, with the check made for them.
    public static WireFrame Make(uint magic, ushort version, ushort kind, uint flags, uint bodyLength, ulong correlationId, ulong streamSequence, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[HeaderSize + payload.Length + Protocol.CheckSize];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), bodyLength);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), correlationId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), streamSequence);
        payload.CopyTo(bytes.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - Protocol.CheckSize), Crc32C.Of(bytes.AsSpan(0, bytes.Length - Protocol.CheckSize)));

        return new WireFrame(bytes);
    }

    // The whole frame at the start of the bytes, when they hold one: the bytes are those of a frame a
    // node of the engine wrote, so its body length is trusted.
    public static bool TryTake(ReadOnlySpan<byte> bytes, out WireFrame frame)
    {
        frame = null!;

        if (bytes.Length < HeaderSize)
        {
            return false;
        }

        var length = HeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);

        if (bytes.Length < length)
        {
            return false;
        }

        frame = new WireFrame(bytes[..(int)length].ToArray());
        return true;
    }

    // Every whole frame in the bytes, in order; a part of a frame at the end is left out.
    public static List<WireFrame> TakeAll(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<WireFrame>();

        while (TryTake(bytes, out var frame))
        {
            frames.Add(frame);
            bytes = bytes[frame.Bytes.Length..];
        }

        return frames;
    }

    // The same frame with another payload, its body length and check made for it; flags and the other
    // fields as they were.
    public WireFrame WithPayload(ReadOnlySpan<byte> payload) =>
        Make(Magic, ProtocolVersion, (ushort)Kind, Flags, (uint)(payload.Length + Protocol.CheckSize), CorrelationId, StreamSequence, payload);

    public WireFrame WithMagic(uint magic) =>
        Make(magic, ProtocolVersion, (ushort)Kind, Flags, BodyLength, CorrelationId, StreamSequence, Payload);

    public WireFrame WithProtocolVersion(ushort version) =>
        Make(Magic, version, (ushort)Kind, Flags, BodyLength, CorrelationId, StreamSequence, Payload);

    public WireFrame WithKind(ushort kind) =>
        Make(Magic, ProtocolVersion, kind, Flags, BodyLength, CorrelationId, StreamSequence, Payload);

    // The header announces another body length; the bytes are those of the frame, so a reader that
    // waited for the body it announces would wait for bytes that do not come.
    public WireFrame WithBodyLength(uint bodyLength) =>
        Make(Magic, ProtocolVersion, (ushort)Kind, Flags, bodyLength, CorrelationId, StreamSequence, Payload);

    // One bit of the check is wrong, nothing else.
    public WireFrame WithDamagedCheck()
    {
        var bytes = Bytes.ToArray();

        bytes[^1] ^= 0x01;
        return new WireFrame(bytes);
    }

    // One bit of the first byte of the payload is wrong, and the check is the one of the frame before.
    public WireFrame WithDamagedPayload()
    {
        var bytes = Bytes.ToArray();

        bytes[HeaderSize] ^= 0x01;
        return new WireFrame(bytes);
    }
}
