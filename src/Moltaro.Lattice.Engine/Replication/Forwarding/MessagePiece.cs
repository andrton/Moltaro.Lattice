using System.Buffers;

namespace Moltaro.Lattice.Replication.Forwarding;

// The bytes one frame of a forwarded write or of a reply carried, copied out of the receive buffer of the
// connection, and a segment of the body they belong to (MessageAssembler).
internal sealed class MessagePiece : ReadOnlySequenceSegment<byte>
{
    public MessagePiece(ReadOnlySpan<byte> bytes, long runningIndex)
    {
        Memory = bytes.ToArray();
        RunningIndex = runningIndex;
    }

    public void Append(MessagePiece next) => Next = next;
}
