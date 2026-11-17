using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Replication;

// The 32 bytes before the body of every frame, as FrameCodec reads them (brief of Epic 1, sprint S10;
// a test asserts the size). On the wire each field is little-endian, in this order; the struct is the
// header read into the numbers of the machine, never the bytes of the wire themselves.
//
// Sequential layout keeps the declared order; every field sits on its natural boundary, so natural
// alignment adds no padding.
[StructLayout(LayoutKind.Sequential)]
internal struct FrameHeader
{
    // Protocol.Magic.
    public uint Magic;

    // Protocol.Version.
    public ushort ProtocolVersion;

    public FrameKindEnum Kind;

    // Protocol.EndsARoot or nothing.
    public uint Flags;

    // The bytes after the header: the payload and the check, at most Protocol.MaxBodyOf the kind.
    public uint BodyLength;

    // Not used by the load: 0. It is there for the answers that the forwarding of writes (S12) pairs
    // with their requests on the same connection.
    public ulong CorrelationId;

    // The number of the frame among the frames its sender sent on the connection: 1 for the first, and
    // one more for each next (FrameConnection).
    public ulong StreamSequence;
}
