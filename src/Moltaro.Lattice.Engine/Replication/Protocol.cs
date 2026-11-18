namespace Moltaro.Lattice.Replication;

// The fixed numbers of the replication transport between a master and its children (brief of Epic 1,
// sprint S10, "Types and members of S10"; the planner's decision of 17 November 2026 after the study of
// S10, point 1). They are rules of the format, the same on every node, not settings: a node that differs
// in one of them is another format and is refused in the handshake.
//
// A frame is a header of HeaderSize bytes and a body of BodyLength bytes. The body is the payload and,
// in its last CheckSize bytes, the check: the CRC32C of the header and of the payload (FrameCheck). The
// header, the check and the bodies of the handshake are little-endian on every machine; only the bytes
// of a root travel in the byte order of the sender, which LayoutFormat announces (Replication.Load).
internal static class Protocol
{
    // The first four bytes of every frame, "LATT" in little-endian order.
    public const uint Magic = 0x5454414C;

    // The version of the frame: its header, its check and the meaning of its kinds.
    public const ushort Version = 1;

    // The bytes of the header of a frame (FrameHeader).
    public const int HeaderSize = 32;

    // The bytes of the check at the end of every body.
    public const int CheckSize = 4;

    // The largest body of a data frame, Schema, Blocks or Transaction, its check included.
    public const int MaxDataBody = 64 * 1024;

    // The largest payload of a data frame.
    public const int MaxDataPayload = MaxDataBody - CheckSize;

    // The largest frame: a header and the largest body.
    public const int MaxFrame = HeaderSize + MaxDataBody;

    // Bit 0 of FrameHeader.Flags: the frame is the last one of a root, or of the schema, or of the
    // whole roots it holds, or of a transaction, or of a forwarded write or its reply. No other flag is
    // defined.
    public const uint EndsABody = 1;

    // The payload of Hello and of HelloReply (FrameCodec.WriteHelloBody): a Guid (16), the history (8),
    // the fingerprint of the schema (32) and four Int32 (16).
    public const int HelloPayloadSize = 72;

    // The payload of Refuse: its reason as a UInt16.
    public const int RefusePayloadSize = 2;

    // The payload of LoadEnd: the start position S of the capture, its end position R and the number of
    // roots sent, each a UInt64 (Load.LoadEndBody).
    public const int LoadEndPayloadSize = 24;

    // The payload of Applied: the sequence up to which the child has applied all it received, a UInt64.
    public const int AppliedPayloadSize = sizeof(ulong);

    // The format of what the bodies of a load and of the stream hold: the envelope of a root, the
    // framing of roots and of the schema, the body of LoadEnd (Replication.Load), the transactions and
    // their effects (Replication.Transactions), and the writes a child forwards with their replies
    // (Replication.Forwarding). Another value is another format. 3 since Epic 1, sprint S12, whose child
    // forwards its writes on the connection; 2 was S11, whose LoadEnd carries S and R and whose stream
    // follows the load.
    public const int WireFormat = 3;

    // The bit of LayoutFormat that a node whose numbers are big-endian sets.
    public const int BigEndianLayout = 0x0001_0000;

    // The format of the bytes of a root as the sender stores them: the version of the root layout
    // (Storage.RootLayout.LayoutVersion) in the low 16 bits, and BigEndianLayout when the machine stores
    // numbers big-endian. A root travels as the bytes of the arena, so a node of the other byte order
    // has another layout format and is refused with Format; no conversion is built (the planner's block
    // of 15 November 2026, "Byte order").
    public static int LayoutFormat => BitConverter.IsLittleEndian ? 1 : 1 | BigEndianLayout;

    // The largest body of a frame of the kind, its check included; 0 for a value that is no kind. A
    // frame whose header announces more is refused from the header alone, before its body is read.
    public static int MaxBodyOf(FrameKindEnum kind) => kind switch
    {
        FrameKindEnum.Hello or FrameKindEnum.HelloReply => HelloPayloadSize + CheckSize,
        FrameKindEnum.Schema or FrameKindEnum.Blocks or FrameKindEnum.Transaction or FrameKindEnum.WriteRequest or FrameKindEnum.WriteReply => MaxDataBody,
        FrameKindEnum.LoadEnd => LoadEndPayloadSize + CheckSize,
        FrameKindEnum.Refuse => RefusePayloadSize + CheckSize,
        FrameKindEnum.Applied => AppliedPayloadSize + CheckSize,
        FrameKindEnum.Closed => CheckSize,
        _ => 0,
    };
}
