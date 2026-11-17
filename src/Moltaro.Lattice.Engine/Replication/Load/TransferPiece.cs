using System.Buffers;

namespace Moltaro.Lattice.Replication.Load;

// One piece of a TransferBuffer, and a segment of the sequence of the buffer while the sequence is read.
internal sealed class TransferPiece : ReadOnlySequenceSegment<byte>
{
    public byte[] Bytes { get; } = new byte[TransferBuffer.PieceSize];

    // The bytes of the piece that hold bytes of the buffer, from its start.
    public int Used { get; set; }

    // Makes the piece the segment of its used bytes that begins at the running index, with no segment
    // after it yet.
    public void Link(long runningIndex)
    {
        Memory = Bytes.AsMemory(0, Used);
        RunningIndex = runningIndex;
        Next = null;
    }

    public void LinkNext(TransferPiece next) => Next = next;
}
