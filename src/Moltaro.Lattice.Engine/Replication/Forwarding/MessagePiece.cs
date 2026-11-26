using System.Buffers;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Replication.Forwarding;

// The bytes one frame of a forwarded write or of a reply carried, copied out of the receive buffer of the
// connection, and a segment of the body they belong to (MessageAssembler).
internal sealed class MessagePiece : ReadOnlySequenceSegment<byte>
{
    private MessagePiece(byte[] bytes, long runningIndex)
    {
        Memory = bytes;
        RunningIndex = runningIndex;
    }

    public static MessagePiece Create(ReadOnlySpan<byte> source, long runningIndex, MemoryLease? lease)
    {
        var bytes = lease?.Allocate<byte>(source.Length, nameof(MessagePiece)) ?? new byte[source.Length];
        source.CopyTo(bytes);
        var capacity = MemoryBudget.ObjectBytes(2L * IntPtr.Size + sizeof(long) + 2 * sizeof(int));
        var budget = lease?.Budget;
        budget?.Reserve(capacity);
        MessagePiece? piece = null;
        try
        {
            piece = new MessagePiece(bytes, runningIndex);
            if (lease is not null)
            {
                budget!.Own(piece, nameof(MessagePiece));
                lease.Own(() => budget.Release(capacity, piece));
            }

            return piece;
        }
        catch
        {
            budget?.Release(capacity, piece);
            throw;
        }
    }

    public void Append(MessagePiece next) => Next = next;
}
